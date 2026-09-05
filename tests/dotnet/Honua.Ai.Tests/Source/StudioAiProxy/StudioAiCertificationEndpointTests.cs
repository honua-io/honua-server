// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Adapters.Bedrock;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>
/// Exercises the real HTTP endpoint, service, provider adapters and signer with
/// deterministic upstream fixtures. These tests do not certify a live model.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class StudioAiCertificationEndpointTests
{
    // RFC 8032 test-only key material. The production signer resolves it through
    // the secret-provider seam; no private material is placed in configuration.
    private const string TestSeed = "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60";
    private const string TestPublicKey = "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a";
    private const string RequestJson = """
        {"messages":[{"role":"user","content":"Read the fixture"}],"certification":{"runNonce":"fixture-run","releaseId":"fixture-release","endpointIdentity":"fixture-proxy","candidateId":"fixture-candidate","actionId":"fixture-action"},"tools":[{"name":"lookup","inputSchema":{"type":"object","properties":{"region":{"type":"string"}}}}]}
        """;
    private const string ExpectedCanonicalRequest = """
        {"certification":{"actionId":"fixture-action","candidateId":"fixture-candidate","endpointIdentity":"fixture-proxy","releaseId":"fixture-release","runNonce":"fixture-run"},"messages":[{"content":"Read the fixture","role":"user"}],"tools":[{"inputSchema":{"properties":{"region":{"type":"string"}},"type":"object"},"name":"lookup"}]}
        """;

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public Task OpenAi_Certification_SignsTheActualEndpointResponse()
        => AssertCertificationAsync("openai");

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public Task Anthropic_Certification_SignsTheActualEndpointResponse()
        => AssertCertificationAsync("anthropic");

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public Task Bedrock_Certification_SignsTheActualEndpointResponse()
        => AssertCertificationAsync("bedrock");

    private static async Task AssertCertificationAsync(string kind)
    {
        var secrets = Substitute.For<ISecretProvider>();
        secrets.IsSecretReference("secret://fixture-signing-key").Returns(true);
        secrets.GetSecretOrDefaultAsync("secret://fixture-signing-key", null, Arg.Any<CancellationToken>())
            .Returns(Convert.ToBase64String(Convert.FromHexString(TestSeed)));
        var upstream = new FixtureHttpHandler(kind);
        var bedrock = new FixtureBedrockClient();
        var factory = Substitute.For<IBedrockChatClientFactory>();
        factory.Create("fixture-model", Arg.Any<string>(), Arg.Any<string?>()).Returns(bedrock);

        await using var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["StudioAiProxy:Enabled"] = "true",
                        ["StudioAiProxy:DefaultProvider"] = "fixture",
                        ["StudioAiProxy:Providers:fixture:Kind"] = kind,
                        ["StudioAiProxy:Providers:fixture:Endpoint"] = "https://fixture.invalid/v1",
                        ["StudioAiProxy:Providers:fixture:Model"] = "fixture-model",
                        ["StudioAiProxy:Providers:fixture:ApiKey"] = "fixture-provider-key",
                        ["StudioAiProxy:TranscriptSigning:KeyId"] = "fixture-signer",
                        ["StudioAiProxy:TranscriptSigning:PrivateKeyReference"] = "secret://fixture-signing-key",
                        ["StudioAiProxy:TranscriptSigning:LifetimeSeconds"] = "900"
                    }));
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<ISecretProvider>();
                services.AddSingleton(secrets);
                services.RemoveAll<IBedrockChatClientFactory>();
                services.AddSingleton(factory);
                services.AddHttpClient("studio-ai-proxy").ConfigurePrimaryHttpMessageHandler(() => upstream);
            });
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var request = new StringContent(RequestJson, Encoding.UTF8, "application/json");
        var before = DateTimeOffset.UtcNow;
        using var response = await client.PostAsync("/api/v1/studio/ai/chat", request);
        var after = DateTimeOffset.UtcNow;
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var events = body.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonSerializer.Deserialize(line[6..], StudioAiProxyJsonContext.Default.StudioAiChatEvent)!)
            .ToArray();
        events.Should().NotContain(evt => evt.Type == StudioAiChatEventType.Error, body);
        events.Should().ContainSingle(evt => evt.Type == StudioAiChatEventType.TranscriptProvenance);
        events.Should().ContainSingle(evt => evt.Type == StudioAiChatEventType.MessageStop);
        (kind == "bedrock" ? bedrock.Calls : upstream.Calls).Should().Be(1);
        var signed = events.Single(evt => evt.Type == StudioAiChatEventType.TranscriptProvenance).Provenance!;
        var bytes = Convert.FromBase64String(signed.CanonicalTranscript);
        var signature = Convert.FromBase64String(signed.Signature);
        Verify(bytes, signature).Should().BeTrue();
        signed.KeyId.Should().Be("fixture-signer");
        signed.TranscriptDigest.Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var envelope = JsonDocument.Parse(bytes);
        var root = envelope.RootElement;
        root.GetProperty("schemaVersion").GetString().Should().Be("honua.studio-ai.transcript.v1");
        root.GetProperty("canonicalization").GetString().Should().Be("honua-canonical-json-v1");
        root.GetProperty("digestAlgorithm").GetString().Should().Be("sha-256");
        root.GetProperty("candidateId").GetString().Should().Be("fixture-candidate");
        root.GetProperty("releaseId").GetString().Should().Be("fixture-release");
        root.GetProperty("endpointIdentity").GetString().Should().Be("fixture-proxy");
        root.GetProperty("actionId").GetString().Should().Be("fixture-action");
        root.GetProperty("runNonce").GetString().Should().Be("fixture-run");
        root.GetProperty("keyId").GetString().Should().Be("fixture-signer");
        root.GetProperty("provider").GetString().Should().Be("fixture");
        root.GetProperty("model").GetString().Should().Be("fixture-model");
        root.GetProperty("selectedResponse").GetString().Should().Be("Aloha");
        root.GetProperty("request").GetBytesFromBase64().Should().Equal(Encoding.UTF8.GetBytes(ExpectedCanonicalRequest));
        root.GetProperty("issuedAt").GetDateTimeOffset().Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        (root.GetProperty("expiresAt").GetDateTimeOffset() - root.GetProperty("issuedAt").GetDateTimeOffset())
            .Should().Be(TimeSpan.FromSeconds(900));
        var eventBytes = root.GetProperty("providerEvents").GetBytesFromBase64();
        root.GetProperty("terminalResultDigest").GetBytesFromBase64().Should().Equal(SHA256.HashData(eventBytes));
        var signedEvents = JsonSerializer.Deserialize(eventBytes, StudioAiProxyJsonContext.Default.ListStudioAiChatEvent)!;
        signedEvents.Should().BeEquivalentTo(events.Where(evt => evt.Type != StudioAiChatEventType.TranscriptProvenance),
            options => options.WithStrictOrdering());
        signedEvents.Where(evt => evt.Type == StudioAiChatEventType.TextDelta).Select(evt => evt.Text).Should().Equal("Aloha");
        var toolStart = signedEvents.Single(evt => evt.Type == StudioAiChatEventType.ToolCallStart);
        toolStart.ToolCallId.Should().Be("call-1");
        toolStart.ToolName.Should().Be("lookup");
        var toolStop = signedEvents.Single(evt => evt.Type == StudioAiChatEventType.ToolCallStop);
        toolStop.ToolCallId.Should().Be("call-1");
        toolStop.ToolArguments!.Value.GetProperty("region").GetString().Should().Be("Maui");
        signedEvents.Single(evt => evt.Type == StudioAiChatEventType.MessageStop).StopReason.Should().Be(StudioAiStopReason.ToolCall);

        // Challenge every signed field independently, retaining the original signature.
        // This catches missing binding coverage, including prompt/events/result substitution.
        Verify(Encoding.UTF8.GetBytes(JsonNode.Parse(bytes)!.ToJsonString()), signature).Should().BeTrue(
            "the mutation writer must preserve the valid encoding before a binding changes");
        foreach (var property in root.EnumerateObject())
        {
            var forged = JsonNode.Parse(bytes)!.AsObject();
            forged[property.Name] = "locally-forged-" + property.Name;
            Verify(Encoding.UTF8.GetBytes(forged.ToJsonString()), signature).Should().BeFalse(
                $"changing signed binding '{property.Name}' must invalidate provenance");
        }
    }

    private static bool Verify(byte[] bytes, byte[] signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(Convert.FromHexString(TestPublicKey), 0));
        verifier.BlockUpdate(bytes, 0, bytes.Length);
        return verifier.VerifySignature(signature);
    }

    private sealed class FixtureHttpHandler(string kind) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            requestBody.Should().Contain("Read the fixture").And.Contain("fixture-model");
            var frames = kind == "openai" ? OpenAiFrames : AnthropicFrames;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(frames, Encoding.UTF8, "text/event-stream")
            };
        }

        private const string OpenAiFrames = """
            data: {"model":"fixture-model","choices":[{"delta":{"content":"Aloha"}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"region\":\"Maui\"}"}}]}}]}

            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;

        private const string AnthropicFrames = """
            event: message_start
            data: {"type":"message_start","message":{"model":"fixture-model"}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Aloha"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"call-1","name":"lookup"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"region\":\"Maui\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
    }

    private sealed class FixtureBedrockClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            messages.Should().ContainSingle(message => message.Text == "Read the fixture");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Aloha");
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["region"] = "Maui" })]);
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.ToolCalls };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

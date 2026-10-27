// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Admission;
using Honua.TestKit.Attributes;
using Honua.TestKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>Connected preparation/HTTP regressions with inert provider transport, never live inference.</summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class StudioAiHttpPreparationTests
{
    [UnitTheory]
    [InlineData("openai", false)]
    [InlineData("openai", true)]
    [InlineData("anthropic", false)]
    [InlineData("anthropic", true)]
    public async Task PrepareChatAsync_AdmissionAwaitDrift_SendsOriginalRouteAndBody(string kind, bool switchDefault)
    {
        var options = new StudioAiProxyProviderOptions
        {
            Kind = kind, Endpoint = "https://original.invalid", Model = "original-model",
            ApiKey = "fixture-key", ApiKeyFromEnvironment = true, MaxTokens = 4096, TimeoutSeconds = 10
        };
        var config = new StudioAiProxyConfiguration { Enabled = true, DefaultProvider = "original" };
        config.Providers["original"] = options;
        config.Providers["later"] = new StudioAiProxyProviderOptions
        {
            Kind = kind, Endpoint = "https://later.invalid", Model = "later-model",
            ApiKey = "later-fixture-key", ApiKeyFromEnvironment = true, MaxTokens = 256, TimeoutSeconds = 10
        };
        foreach (var (name, provider) in config.Providers)
        {
            config.Disclosure.Approvals.Add(new()
            {
                Provider = name, Kind = kind, Destination = provider.Endpoint, Model = provider.Model,
                UseCase = "studio-chat", DataClassification = "unclassified"
            });
        }
        config.Disclosure.Approvals.Add(new()
        {
            Provider = "original", Kind = kind, Destination = "https://mutated.invalid", Model = "mutated-model",
            UseCase = "studio-chat", DataClassification = "unclassified"
        });
        var messages = new List<StudioAiMessage> { new() { Role = StudioAiRole.User, Content = "original input" } };
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"description\":\"original schema\"}");
        var tools = new List<StudioAiToolDefinition> { new() { Name = "original_tool", InputSchema = schema.RootElement } };
        var request = new StudioAiChatRequest { Messages = messages, Tools = tools, MaxTokens = 8192 };
        using var handler = new CapturingHandler(kind);
        using var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        var admission = new MutatingAdmission(() =>
        {
            handler.Sends.Should().Be(0);
            config.DefaultProvider = switchDefault ? "later" : "original";
            options.Model = "mutated-model";
            options.Endpoint = "https://mutated.invalid";
            options.MaxTokens = 512;
            messages[0] = new() { Role = StudioAiRole.User, Content = "later input" };
            tools.Clear();
        });
        var service = Service(config, Adapter(kind, factory), admission);
        await using var prepared = await service.PrepareChatAsync(request, RecordingAiAdmission.Context(), default);
        var events = new List<StudioAiChatEvent>();
        await foreach (var evt in service.StreamPreparedChatAsync(prepared, new(), default)) events.Add(evt);

        events.Should().Contain(evt => evt.Type == StudioAiChatEventType.MessageStop);
        handler.Sends.Should().Be(1);
        handler.Destination.Should().Be("https://original.invalid" + (kind == "openai" ? "/chat/completions" : "/v1/messages"));
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("model").GetString().Should().Be("original-model");
        body.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(8192, "a permitted override is not clamped to the configured default");
        handler.Body.Should().Contain("original input").And.Contain("original_tool").And.Contain("original schema").And.NotContain("later input");
    }

    [UnitTest]
    public async Task PrepareChatAsync_OriginalAcceptedBytesChanged_SignsOriginalAcceptedWire()
    {
        var config = new StudioAiProxyConfiguration { Enabled = true, DefaultProvider = "local" };
        config.Providers["local"] = new()
        {
            Kind = "openai", Endpoint = "https://original.invalid", Model = "original-model", MaxTokens = 4096
        };
        config.Disclosure.Approvals.Add(new()
        {
            Provider = "local", Kind = "openai", Destination = "https://original.invalid", Model = "original-model",
            UseCase = "studio-chat", DataClassification = "unclassified"
        });
        var accepted = Encoding.UTF8.GetBytes("""
            {
              "messages": [{"role": "user", "content": "original input"}],
              "tools": [{"name": "original_tool", "inputSchema": {"type": "object"}}],
              "certification": {
                "candidateId": "original-candidate", "tenantId": "original-tenant", "releaseId": "original-release",
                "endpointIdentity": "original-endpoint", "actionId": "original-action", "runNonce": "original-nonce"
              }
            }
            """);
        var original = (byte[])accepted.Clone();
        var wire = JsonSerializer.Deserialize(accepted, StudioAiProxyJsonContext.Default.StudioAiChatHttpRequest)!;
        var (request, mappingError) = StudioAiChatRequestMapper.ToDomain(wire, acceptedRequestJson: accepted,
            certificationTenantId: "original-tenant");
        mappingError.Should().BeNull();
        request.Should().NotBeNull();
        using var handler = new CapturingHandler("openai");
        using var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        config.TranscriptSigning.KeyId = "fixture-signing-key";
        config.TranscriptSigning.PrivateKeyReference = "secret://fixture-signing-key";
        var secrets = Substitute.For<ISecretProvider>();
        secrets.IsSecretReference("secret://fixture-signing-key").Returns(true);
        secrets.GetSecretOrDefaultAsync("secret://fixture-signing-key", null, Arg.Any<CancellationToken>())
            .Returns(Convert.ToBase64String(new byte[32]));
        var service = Service(config, Adapter("openai", factory), new MutatingAdmission(() =>
        {
            var offset = Encoding.UTF8.GetString(accepted).IndexOf("original input", StringComparison.Ordinal);
            Encoding.UTF8.GetBytes("modified input").CopyTo(accepted, offset);
        }), secrets);
        await using var prepared = await service.PrepareChatAsync(request!, RecordingAiAdmission.Context(), default);
        var events = new List<StudioAiChatEvent>();
        await foreach (var evt in service.StreamPreparedChatAsync(prepared, new(), default)) events.Add(evt);
        events.Should().Contain(evt => evt.Type == StudioAiChatEventType.MessageStop);
        handler.Sends.Should().Be(1);
        handler.Body.Should().Contain("original_tool");
        var provenance = events.Where(evt => evt.Type == StudioAiChatEventType.TranscriptProvenance)
            .Should().ContainSingle().Which.Provenance;
        provenance.Should().NotBeNull();
        using var transcript = JsonDocument.Parse(Convert.FromBase64String(provenance!.CanonicalTranscript));
        var signedRequest = Convert.FromBase64String(transcript.RootElement.GetProperty("request").GetString()!);
        signedRequest.Should().Equal(StudioAiTranscriptSigner.Canonicalize(original),
            "accepted-wire identity must stay original, without inserting resolved defaults");
        transcript.RootElement.GetProperty("candidateId").GetString().Should().Be("original-candidate");
        prepared.Request.AcceptedRequestJson.Should().Equal(original);
        prepared.Request.AcceptedRequestJson.Should().NotBeSameAs(accepted);
    }

    [UnitTheory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task PrepareChatAsync_CallerSchemaDisposed_StillSendsOwnedToolSchema(string kind)
    {
        var config = new StudioAiProxyConfiguration { Enabled = true, DefaultProvider = "local" };
        config.Providers["local"] = new()
        {
            Kind = kind, Endpoint = "https://original.invalid", Model = "original-model",
            ApiKey = "fixture-key", ApiKeyFromEnvironment = true, MaxTokens = 4096, TimeoutSeconds = 10
        };
        config.Disclosure.Approvals.Add(new()
        {
            Provider = "local", Kind = kind, Destination = config.Providers["local"].Endpoint,
            Model = "original-model", UseCase = "studio-chat", DataClassification = "unclassified"
        });
        var schema = JsonDocument.Parse("{\"type\":\"object\",\"description\":\"owned schema\"}");
        var request = new StudioAiChatRequest
        {
            Messages = [new() { Role = StudioAiRole.User, Content = "original input" }],
            Tools = [new() { Name = "owned_tool", InputSchema = schema.RootElement }]
        };
        using var handler = new CapturingHandler(kind);
        using var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        var service = Service(config, Adapter(kind, factory), new MutatingAdmission(schema.Dispose));
        await using var prepared = await service.PrepareChatAsync(request, RecordingAiAdmission.Context(), default);
        var events = new List<StudioAiChatEvent>();
        await foreach (var evt in service.StreamPreparedChatAsync(prepared, new(), default)) events.Add(evt);
        events.Should().Contain(evt => evt.Type == StudioAiChatEventType.MessageStop);
        handler.Sends.Should().Be(1);
        handler.Body.Should().Contain("owned_tool").And.Contain("owned schema");
    }

    private static StudioAiProxyService Service(StudioAiProxyConfiguration config, IStudioAiProxyAdapter adapter, IAiExecutionAdmission admission,
        ISecretProvider? secrets = null)
        => new(Options.Create(config), [adapter], new StudioAiTranscriptSigner(Options.Create(config), TimeProvider.System, secrets),
            NullLogger<StudioAiProxyService>.Instance, admission);

    private static IStudioAiProxyAdapter Adapter(string kind, IHttpClientFactory factory)
        => kind == "openai"
            ? new OpenAiCompatibleStudioAiProxyAdapter(factory, new StudioAiProxyApiKeyResolver(), NullLogger<OpenAiCompatibleStudioAiProxyAdapter>.Instance)
            : new AnthropicStudioAiProxyAdapter(factory, new StudioAiProxyApiKeyResolver(), NullLogger<AnthropicStudioAiProxyAdapter>.Instance);

    private sealed class MutatingAdmission(Action mutate) : IAiExecutionAdmission
    {
        private readonly RecordingAiAdmission _inner = new();
        public async Task<IAiExecutionPermit> AcquireAsync(AiExecutionContext context, CancellationToken cancellationToken)
        {
            await Task.Yield();
            mutate();
            return await _inner.AcquireAsync(context, cancellationToken);
        }
    }

    private sealed class CapturingHandler(string kind) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        public string? Destination { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            Destination = request.RequestUri!.AbsoluteUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = kind == "openai"
                ? "data: {\"model\":\"original-model\",\"choices\":[{\"delta\":{\"content\":\"observed\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                : "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"model\":\"original-model\"}}\n\nevent: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "text/event-stream") };
        }
    }
}

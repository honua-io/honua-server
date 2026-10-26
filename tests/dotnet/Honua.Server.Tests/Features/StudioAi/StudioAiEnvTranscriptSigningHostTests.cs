// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Ai.StudioAiProxy;
using Honua.Core.Features.Security.Admission;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Security;
using Honua.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Honua.Server.Tests.Features.StudioAi;

/// <summary>
/// Production-host proof for the terminal-model canary configuration: the candidate boots with
/// <c>StudioAiProxy:TranscriptSigning:PrivateKeyReference=env://HONUA_CANARY_TRANSCRIPT_SIGNING_SEED</c>
/// and the real composed <c>ISecretProvider</c> (no substituted secret provider). The capabilities
/// response must publish the required transcript-signing manifest for the configured key id, and a
/// certification chat must emit a transcript signed by exactly that key.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class StudioAiEnvTranscriptSigningHostTests : IAsyncLifetime
{
    private const string SeedVariable = "HONUA_CANARY_TRANSCRIPT_SIGNING_SEED";
    private const string KeyId = "terminal-model-canary";
    private const string ProviderName = "env-signing-deterministic";
    private const string Model = "deterministic-env-signing-model";

    private readonly byte[] _publicKey;
    private readonly string _encodedSeed;
    private readonly string? _previousSeed;
    private readonly WebAppFixture _fixture;

    public StudioAiEnvTranscriptSigningHostTests()
    {
        var seed = new byte[Ed25519PrivateKeyParameters.KeySize];
        new SecureRandom().NextBytes(seed);
        _publicKey = new Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded();
        _encodedSeed = Convert.ToBase64String(seed);
        CryptographicOperations.ZeroMemory(seed);

        _previousSeed = Environment.GetEnvironmentVariable(SeedVariable);
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStudioAiProxyAdapter>();
                services.AddSingleton<IStudioAiProxyAdapter>(new DeterministicTextAdapter());
            })
            .ConfigureWebHost(ConfigureHost);
    }

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(SeedVariable, _encodedSeed);
        return _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        Environment.SetEnvironmentVariable(SeedVariable, _previousSeed);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/ai/capabilities")]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public async Task EnvSeedReference_PublishesRequiredManifest_AndSignsCertificationTranscript()
    {
        using var client = _fixture.CreateAdminClient();

        using var capabilitiesResponse = await client.GetAsync("/api/v1/studio/ai/capabilities");
        capabilitiesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var capabilitiesBody = await capabilitiesResponse.Content.ReadAsStringAsync();
        capabilitiesBody.Should().NotContain(_encodedSeed, "the private seed must never be published");
        using var capabilities = JsonDocument.Parse(capabilitiesBody);
        var manifest = capabilities.RootElement.GetProperty("data").GetProperty("transcriptSigning");
        manifest.GetProperty("requiredForCertification").GetBoolean().Should().BeTrue();
        var keys = manifest.GetProperty("keys");
        keys.GetArrayLength().Should().Be(1);
        var key = keys[0];
        key.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            ["keyId", "algorithm", "publicKey", "fingerprint", "revoked"],
            "null validity bounds are omitted, so the canary's canonical manifest digest is over exactly these fields");
        key.GetProperty("keyId").GetString().Should().Be(KeyId);
        key.GetProperty("algorithm").GetString().Should().Be("Ed25519");
        key.GetProperty("revoked").GetBoolean().Should().BeFalse();
        key.GetProperty("publicKey").GetString().Should().Be(Convert.ToBase64String(_publicKey));
        key.GetProperty("fingerprint").GetString().Should().Be(
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(_publicKey)));

        using var chatResponse = await client.PostAsJsonAsync("/api/v1/studio/ai/chat", new
        {
            certification = new
            {
                candidateId = "candidate-env-signing",
                releaseId = "2026.1-rc.3",
                endpointIdentity = "/api/v1/studio/ai/chat",
                actionId = "terminal-model-canary-probe",
                runNonce = $"run-{Guid.NewGuid():N}"
            },
            messages = new[] { new { role = "user", content = "sign this turn" } }
        });

        chatResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await chatResponse.Content.ReadAsStringAsync();
        body.Should().NotContain(StudioAiTranscriptSigner.UnavailableCode);
        body.Should().NotContain(_encodedSeed);
        var provenance = ReadProvenance(body);
        provenance.Should().NotBeNull("an env:// signing reference must produce a signed transcript");
        provenance!.KeyId.Should().Be(KeyId);

        var transcript = Convert.FromBase64String(provenance.CanonicalTranscript);
        Convert.ToHexStringLower(SHA256.HashData(transcript)).Should().Be(provenance.TranscriptDigest);
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(_publicKey, 0));
        verifier.BlockUpdate(transcript, 0, transcript.Length);
        verifier.VerifySignature(Convert.FromBase64String(provenance.Signature)).Should().BeTrue(
            "the transcript must verify against the key the capabilities manifest published");
        using var envelope = JsonDocument.Parse(transcript);
        envelope.RootElement.GetProperty("keyId").GetString().Should().Be(KeyId);
        envelope.RootElement.GetProperty("provider").GetString().Should().Be(ProviderName);
        envelope.RootElement.GetProperty("model").GetString().Should().Be(Model);
    }

    private static StudioAiSignedTranscript? ReadProvenance(string body)
    {
        foreach (var lines in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(frame => frame.Split('\n')))
        {
            var eventLine = lines.FirstOrDefault(line => line.StartsWith("event: ", StringComparison.Ordinal));
            var dataLine = lines.FirstOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal));
            if (eventLine is null || dataLine is null || eventLine[7..] != "transcript_provenance")
            {
                continue;
            }

            return JsonSerializer.Deserialize(dataLine[6..], StudioAiProxyJsonContext.Default.StudioAiChatEvent)?.Provenance;
        }

        return null;
    }

    private static void ConfigureHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        builder.ConfigureServices(services => services.AddSingleton<IAiExecutionAdmission, RecordingAiAdmission>());
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AiExecutionAdmission:DeploymentId"] = "test-deployment",
                ["StudioAiProxy:Enabled"] = "true",
                ["StudioAiProxy:Disclosure:Approvals:0:Provider"] = ProviderName,
                ["StudioAiProxy:Disclosure:Approvals:0:Kind"] = StudioAiProxyConfiguration.OpenAiKind,
                ["StudioAiProxy:Disclosure:Approvals:0:Destination"] = "https://deterministic.invalid/v1",
                ["StudioAiProxy:Disclosure:Approvals:0:Model"] = Model,
                ["StudioAiProxy:Disclosure:Approvals:0:UseCase"] = "studio-chat",
                ["StudioAiProxy:Disclosure:Approvals:0:DataClassification"] = "unclassified",
                ["StudioAiProxy:DefaultProvider"] = ProviderName,
                [$"StudioAiProxy:Providers:{ProviderName}:Kind"] = StudioAiProxyConfiguration.OpenAiKind,
                [$"StudioAiProxy:Providers:{ProviderName}:Endpoint"] = "https://deterministic.invalid/v1",
                [$"StudioAiProxy:Providers:{ProviderName}:Model"] = Model,
                [$"StudioAiProxy:Providers:{ProviderName}:ApiKey"] = "test-key",
                ["StudioAiProxy:TranscriptSigning:KeyId"] = KeyId,
                ["StudioAiProxy:TranscriptSigning:PrivateKeyReference"] = "env://" + SeedVariable,
            }));
    }

    private sealed class DeterministicTextAdapter : IStudioAiProxyAdapter
    {
        public string Kind => StudioAiProxyConfiguration.OpenAiKind;

        public bool IsConfigured(string providerName, StudioAiProxyProviderOptions options) => true;

        public async IAsyncEnumerable<StudioAiChatEvent> StreamAsync(
            StudioAiProxyProviderOptions options,
            StudioAiChatRequest request,
            IAiExecutionPermit permit,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStart, Model = options.Model };
            yield return new StudioAiChatEvent { Type = StudioAiChatEventType.TextDelta, Text = "signed response" };
            yield return new StudioAiChatEvent
            {
                Type = StudioAiChatEventType.MessageStop,
                StopReason = StudioAiStopReason.EndTurn
            };
            await Task.CompletedTask;
        }
    }
}

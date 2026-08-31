// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Honua.Server.Tests.Features.StudioAi;

public sealed class StudioAiTranscriptSignerTests
{
    [Fact]
    public void Canonicalize_UsesOrdinalObjectOrderAndRejectsDuplicateKeys()
    {
        var canonical = StudioAiTranscriptSigner.Canonicalize(
            """{"z":1,"nested":{"b":2,"a":1},"array":[{"d":4,"c":3}]}"""u8);

        System.Text.Encoding.UTF8.GetString(canonical).Should().Be(
            """{"array":[{"c":3,"d":4}],"nested":{"a":1,"b":2},"z":1}""");
        var duplicate = () => StudioAiTranscriptSigner.Canonicalize("""{"a":1,"a":2}"""u8);
        duplicate.Should().Throw<JsonException>().WithMessage("*Duplicate JSON property*");
    }

    [Fact]
    public async Task Sign_UsesResolvedThrowawayKey_AndBindsEveryCertificationIdentity()
    {
        var seed = new byte[Ed25519PrivateKeyParameters.KeySize];
        new SecureRandom().NextBytes(seed);
        var provider = SecretProvider(Convert.ToBase64String(seed));
        CryptographicOperations.ZeroMemory(seed);
        var signer = CreateSigner(provider);
        var key = await signer.ResolveKeyAsync(CancellationToken.None);

        key.Should().NotBeNull();
        var signed = signer.Sign(key!, Request(), "provider-a", "model-v1", Events());
        var canonical = Convert.FromBase64String(signed.CanonicalTranscript);
        using var envelope = JsonDocument.Parse(canonical);
        var root = envelope.RootElement;
        root.GetProperty("candidateId").GetString().Should().Be("candidate-7");
        root.GetProperty("releaseId").GetString().Should().Be("release-9");
        root.GetProperty("endpointIdentity").GetString().Should().Be("honua.example/api/v1/studio/ai/chat");
        root.GetProperty("actionId").GetString().Should().Be("compose-map");
        root.GetProperty("runNonce").GetString().Should().Be("run-nonce-unique");
        root.GetProperty("provider").GetString().Should().Be("provider-a");
        root.GetProperty("model").GetString().Should().Be("model-v1");
        signed.TranscriptDigest.Should().Be(Convert.ToHexStringLower(SHA256.HashData(canonical)));

        Verify(key!.PublicKey, canonical, Convert.FromBase64String(signed.Signature)).Should().BeTrue();
        canonical[^2] ^= 1;
        Verify(key.PublicKey, canonical, Convert.FromBase64String(signed.Signature)).Should().BeFalse(
            "post-signature mutation must invalidate the detached signature");
    }

    [Fact]
    public async Task ResolveKey_MissingOrInlineMaterial_ReturnsNull()
    {
        var provider = Substitute.For<ISecretProvider>();
        provider.IsSecretReference(Arg.Any<string?>()).Returns(false);
        var signer = CreateSigner(provider);

        (await signer.ResolveKeyAsync(CancellationToken.None)).Should().BeNull();
        await provider.DidNotReceiveWithAnyArgs().GetSecretAsync(default!, default);
    }

    [Fact]
    public async Task ResolveKey_SecretProviderFailure_ReturnsNullWithoutFallback()
    {
        var provider = Substitute.For<ISecretProvider>();
        provider.IsSecretReference("secret://studio/transcript-signing-key").Returns(true);
        provider.GetSecretOrDefaultAsync(
                "secret://studio/transcript-signing-key",
                null,
                Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => throw new InvalidOperationException("reference resolution failed"));
        var signer = CreateSigner(provider);

        var key = await signer.ResolveKeyAsync(CancellationToken.None);

        key.Should().BeNull("a provider failure must never cause an in-process fallback key to be generated");
    }

    [Fact]
    public async Task GetManifest_PublishesActiveAndOverlapFingerprints_WithoutPrivateMaterial()
    {
        var activeSeed = new byte[Ed25519PrivateKeyParameters.KeySize];
        var overlapSeed = new byte[Ed25519PrivateKeyParameters.KeySize];
        new SecureRandom().NextBytes(activeSeed);
        new SecureRandom().NextBytes(overlapSeed);
        var overlapPublic = new Ed25519PrivateKeyParameters(overlapSeed, 0).GeneratePublicKey().GetEncoded();
        var provider = SecretProvider(Convert.ToBase64String(activeSeed));
        var options = Options.Create(Configuration());
        options.Value.TranscriptSigning.OverlapKeys.Add(new StudioAiTranscriptVerificationKeyOptions
        {
            KeyId = "previous-2026-07",
            PublicKey = Convert.ToBase64String(overlapPublic),
            NotBefore = DateTimeOffset.Parse("2026-08-15T00:00:00Z", null, System.Globalization.DateTimeStyles.RoundtripKind),
            NotAfter = DateTimeOffset.Parse("2026-09-15T00:00:00Z", null, System.Globalization.DateTimeStyles.RoundtripKind)
        });
        CryptographicOperations.ZeroMemory(activeSeed);
        CryptographicOperations.ZeroMemory(overlapSeed);
        var signer = new StudioAiTranscriptSigner(options, TimeProvider.System, provider);

        var manifest = await signer.GetManifestAsync(CancellationToken.None);

        manifest.Keys.Should().HaveCount(2);
        manifest.Keys.Should().OnlyContain(k => k.Fingerprint.StartsWith("sha256:", StringComparison.Ordinal));
        JsonSerializer.Serialize(manifest).ToLowerInvariant().Should().NotContain("privatekey");
    }

    private static StudioAiTranscriptSigner CreateSigner(ISecretProvider provider)
        => new(Options.Create(Configuration()), TimeProvider.System, provider);

    private static StudioAiProxyConfiguration Configuration() => new()
    {
        TranscriptSigning = new StudioAiTranscriptSigningOptions
        {
            KeyId = "active-2026-08",
            PrivateKeyReference = "secret://studio/transcript-signing-key"
        }
    };

    private static ISecretProvider SecretProvider(string encodedSeed)
    {
        var provider = Substitute.For<ISecretProvider>();
        provider.IsSecretReference("secret://studio/transcript-signing-key").Returns(true);
        provider.GetSecretOrDefaultAsync(
                "secret://studio/transcript-signing-key",
                null,
                Arg.Any<CancellationToken>())
            .Returns(encodedSeed);
        return provider;
    }

    private static StudioAiChatRequest Request() => new()
    {
        Certification = new StudioAiTranscriptCertification
        {
            CandidateId = "candidate-7",
            ReleaseId = "release-9",
            EndpointIdentity = "honua.example/api/v1/studio/ai/chat",
            ActionId = "compose-map",
            RunNonce = "run-nonce-unique"
        },
        System = "system prompt",
        Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "request bytes" }]
    };

    private static IReadOnlyList<StudioAiChatEvent> Events() =>
    [
        new() { Type = StudioAiChatEventType.MessageStart, Model = "model-v1" },
        new() { Type = StudioAiChatEventType.TextDelta, Text = "selected response" },
        new() { Type = StudioAiChatEventType.MessageStop, StopReason = StudioAiStopReason.EndTurn }
    ];

    private static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }
}

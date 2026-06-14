// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.LicenseMint;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Licensing;

/// <summary>
/// Round-trip correctness tests for the license-mint tool (<c>Honua.LicenseMint</c>).
/// These are the lockstep guarantee between minting and the runtime verifier: a
/// freshly generated key pair mints an envelope, that envelope is configured as a
/// trusted key and loaded through the real <see cref="FileBackedLicenseService"/>
/// (the production verifier path), and the runtime must accept the right edition
/// while rejecting tampering, wrong keys, and expiry.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class LicenseMintRoundTripTests
{
    private const string KeyId = "honua-mint-test";

    [UnitTest]
    public async Task Mint_GeneratedKeyPair_RuntimeAcceptsProLicense()
    {
        var keyPair = LicenseSigningKeyPair.Generate();
        var result = LicenseMinter.Mint(
            new LicenseMintRequest
            {
                KeyId = KeyId,
                LicenseId = "lic-mint-pro",
                LicensedTo = "Mint RoundTrip Operator",
                Edition = HonuaEdition.Pro,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(365)
            },
            keyPair);

        var snapshot = await ValidateAsync(result.EnvelopeJson, keyPair.PublicKeyTrustedKeySetting());

        snapshot.ValidationState.Should().Be(LicenseValidationState.Valid);
        snapshot.IsValid.Should().BeTrue();
        snapshot.Edition.Should().Be(HonuaEdition.Pro);
        snapshot.LicenseId.Should().Be("lic-mint-pro");
        snapshot.LicensedTo.Should().Be("Mint RoundTrip Operator");
        // Pro defaults grant every catalog feature at or below Pro and gate Enterprise features.
        snapshot.HasEntitlement(FeatureCatalog.FeatureServerEditsKey).Should().BeTrue();
        snapshot.HasEntitlement("analytics.clustering").Should().BeTrue();
        snapshot.HasEntitlement(FeatureCatalog.BranchVersioningKey).Should().BeFalse();
        snapshot.HasEntitlement(FeatureCatalog.PluginSdkKey).Should().BeFalse();
    }

    [UnitTest]
    public async Task Mint_EnterpriseEdition_RuntimeGrantsEnterpriseEntitlements()
    {
        var keyPair = LicenseSigningKeyPair.Generate();
        var result = LicenseMinter.Mint(
            new LicenseMintRequest
            {
                KeyId = KeyId,
                LicenseId = "lic-mint-ent",
                LicensedTo = "Enterprise Operator",
                Edition = HonuaEdition.Enterprise
            },
            keyPair);

        var snapshot = await ValidateAsync(result.EnvelopeJson, keyPair.PublicKeyTrustedKeySetting());

        snapshot.ValidationState.Should().Be(LicenseValidationState.Valid);
        snapshot.Edition.Should().Be(HonuaEdition.Enterprise);
        snapshot.HasEntitlement(FeatureCatalog.BranchVersioningKey).Should().BeTrue();
        snapshot.HasEntitlement(FeatureCatalog.PluginSdkKey).Should().BeTrue();
        snapshot.ExpiresAt.Should().BeNull("a perpetual license omits expiresAt");
    }

    [UnitTest]
    public async Task Mint_ExplicitEntitlements_RuntimeActivatesOnlyThoseKeys()
    {
        var keyPair = LicenseSigningKeyPair.Generate();
        var result = LicenseMinter.Mint(
            new LicenseMintRequest
            {
                KeyId = KeyId,
                LicenseId = "lic-mint-scoped",
                LicensedTo = "Scoped Operator",
                Edition = HonuaEdition.Pro,
                Entitlements = ["analytics.clustering", "staticmap.high-dpi"]
            },
            keyPair);

        var snapshot = await ValidateAsync(result.EnvelopeJson, keyPair.PublicKeyTrustedKeySetting());

        snapshot.ValidationState.Should().Be(LicenseValidationState.Valid);
        snapshot.HasEntitlement("analytics.clustering").Should().BeTrue();
        snapshot.HasEntitlement("staticmap.high-dpi").Should().BeTrue();
        snapshot.HasEntitlement("analytics.spatial-join").Should().BeFalse();
    }

    [UnitTest]
    public async Task Mint_TamperedPayload_RuntimeRejectsSignature()
    {
        var keyPair = LicenseSigningKeyPair.Generate();
        var result = LicenseMinter.Mint(
            new LicenseMintRequest
            {
                KeyId = KeyId,
                LicenseId = "lic-mint-tamper",
                LicensedTo = "Tamper Operator",
                Edition = HonuaEdition.Pro
            },
            keyPair);

        // Flip one byte of the envelope's base64url payload to simulate tampering.
        var tampered = TamperPayloadByte(result.EnvelopeJson);

        var snapshot = await ValidateAsync(tampered, keyPair.PublicKeyTrustedKeySetting());

        snapshot.IsValid.Should().BeFalse();
        snapshot.ValidationState.Should().Be(LicenseValidationState.InvalidSignature);
        snapshot.Edition.Should().Be(HonuaEdition.Community);
    }

    [UnitTest]
    public async Task Mint_VerifiedWithDifferentPublicKey_RuntimeRejectsSignature()
    {
        var signingKeyPair = LicenseSigningKeyPair.Generate();
        var otherKeyPair = LicenseSigningKeyPair.Generate();
        var result = LicenseMinter.Mint(
            new LicenseMintRequest
            {
                KeyId = KeyId,
                LicenseId = "lic-mint-wrongkey",
                LicensedTo = "Wrong Key Operator",
                Edition = HonuaEdition.Pro
            },
            signingKeyPair);

        // Trust a different public key under the same keyId — signature must fail.
        var snapshot = await ValidateAsync(result.EnvelopeJson, otherKeyPair.PublicKeyTrustedKeySetting());

        snapshot.IsValid.Should().BeFalse();
        snapshot.ValidationState.Should().Be(LicenseValidationState.InvalidSignature);
    }

    [UnitTest]
    public void Mint_AlreadyExpiredTerm_IsRejectedAtMintTime()
    {
        var keyPair = LicenseSigningKeyPair.Generate();

        var act = () => LicenseMinter.Mint(
            new LicenseMintRequest
            {
                KeyId = KeyId,
                LicenseId = "lic-mint-expired",
                LicensedTo = "Expired Operator",
                Edition = HonuaEdition.Pro,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
            },
            keyPair);

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public async Task Mint_ValidSignatureButExpiredEnvelope_RuntimeRejectsExpired()
    {
        // The mint guard forbids minting an expired license, but a long-lived license
        // can still age out. Build a valid-signature envelope whose expiry is in the
        // past by signing the canonical bytes directly with the mint primitives.
        var keyPair = LicenseSigningKeyPair.Generate();
        var result = LicenseMinter.Mint(
            new LicenseMintRequest
            {
                KeyId = KeyId,
                LicenseId = "lic-mint-aged",
                LicensedTo = "Aged Operator",
                Edition = HonuaEdition.Pro,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(1)
            },
            keyPair);

        // Wait past expiry so the runtime sees a valid signature but a past expiresAt.
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        var snapshot = await ValidateAsync(result.EnvelopeJson, keyPair.PublicKeyTrustedKeySetting());

        snapshot.IsValid.Should().BeFalse();
        snapshot.ValidationState.Should().Be(LicenseValidationState.Expired);
        snapshot.LicenseId.Should().Be("lic-mint-aged");
    }

    [UnitTest]
    public void Keygen_RegeneratesSamePublicKeyFromSeed()
    {
        var keyPair = LicenseSigningKeyPair.Generate();
        var reconstructed = LicenseSigningKeyPair.FromPrivateSeed(keyPair.PrivateSeed);

        reconstructed.PublicKey.Should().Equal(keyPair.PublicKey);
        reconstructed.PublicKeyTrustedKeySetting().Should().Be(keyPair.PublicKeyTrustedKeySetting());
    }

    private static async Task<LicenseSnapshot> ValidateAsync(byte[] envelopeJson, string trustedKeySetting)
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var licensePath = Path.Combine(tempDirectory.FullName, "license.honua-license.json");
        await File.WriteAllBytesAsync(licensePath, envelopeJson);

        var options = new LicenseOptions
        {
            LicensePath = licensePath,
            TrustedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [KeyId] = trustedKeySetting
            }
        };

        var service = new FileBackedLicenseService(
            Options.Create(options),
            new BouncyCastleEd25519Verifier(),
            NullLogger<FileBackedLicenseService>.Instance);
        await service.StartAsync(CancellationToken.None);
        return service.GetSnapshot();
    }

    private static byte[] TamperPayloadByte(byte[] envelopeJson)
    {
        var text = System.Text.Encoding.UTF8.GetString(envelopeJson);
        using var document = System.Text.Json.JsonDocument.Parse(text);
        var root = document.RootElement;
        var version = root.GetProperty("version").GetInt32();
        var keyId = root.GetProperty("keyId").GetString();
        var payload = root.GetProperty("payload").GetString()!;
        var signature = root.GetProperty("signature").GetString();

        // Flip a character in the middle of the base64url payload.
        var chars = payload.ToCharArray();
        var index = chars.Length / 2;
        chars[index] = chars[index] == 'A' ? 'B' : 'A';
        var tamperedPayload = new string(chars);

        var rebuilt = System.Text.Json.JsonSerializer.Serialize(new
        {
            version,
            keyId,
            payload = tamperedPayload,
            signature
        });
        return System.Text.Encoding.UTF8.GetBytes(rebuilt);
    }
}

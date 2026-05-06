// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Honua.Server.Tests.Features.Licensing;

internal static class LicenseTestSupport
{
    private static readonly byte[] SigningSeed =
    [
        0x10, 0x42, 0x9A, 0xC1, 0x7E, 0x6D, 0x55, 0x3A,
        0x20, 0x31, 0x48, 0x59, 0x6A, 0x7B, 0x8C, 0x9D,
        0xAE, 0xBF, 0xC0, 0xD1, 0xE2, 0xF3, 0x04, 0x15,
        0x26, 0x37, 0x48, 0x59, 0x6A, 0x7B, 0x8C, 0x9D
    ];

    public const string KeyId = "test-key";

    public static SignedLicenseTestFile CreateSignedLicense(
        HonuaEdition edition,
        DateTimeOffset? expiresAt = null,
        string[]? entitlements = null,
        string? keyId = null,
        bool tamperSignature = false)
    {
        var privateKey = new Ed25519PrivateKeyParameters(SigningSeed, 0);
        var publicKey = privateKey.GeneratePublicKey().GetEncoded();
        var payload = JsonSerializer.Serialize(new
        {
            schema = "honua.license/v1",
            licenseId = "lic-test-338",
            licensedTo = "Honua Test Operator",
            edition = edition.ToString(),
            issuedAt = DateTimeOffset.UtcNow.AddDays(-1),
            expiresAt,
            entitlements = entitlements ?? ActiveEntitlementsFor(edition)
        });
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        signer.BlockUpdate(payloadBytes, 0, payloadBytes.Length);
        var signature = signer.GenerateSignature();
        if (tamperSignature)
        {
            signature[0] ^= 0xFF;
        }

        var envelope = JsonSerializer.Serialize(new
        {
            version = 1,
            keyId = keyId ?? KeyId,
            payload = Base64Url(payloadBytes),
            signature = Base64Url(signature)
        });

        return new SignedLicenseTestFile(
            Encoding.UTF8.GetBytes(envelope),
            "base64url:" + Base64Url(publicKey));
    }

    public static string[] ActiveEntitlementsFor(HonuaEdition edition)
        => FeatureCatalog.All
            .Where(feature => feature.MinimumEdition <= edition)
            .Select(feature => feature.Key)
            .ToArray();

    public static LicenseSnapshot CreateSnapshot(
        HonuaEdition edition,
        LicenseValidationState validationState = LicenseValidationState.Valid,
        string[]? entitlements = null)
    {
        var activeKeys = (entitlements ?? ActiveEntitlementsFor(edition))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalogEntitlements = FeatureCatalog.All
            .Select(feature => new Entitlement
            {
                Key = feature.Key,
                Name = feature.DisplayName,
                IsActive = activeKeys.Contains(feature.Key)
            })
            .ToArray();

        return new LicenseSnapshot(
            edition,
            validationState == LicenseValidationState.Valid,
            validationState,
            DateTimeOffset.UtcNow.AddDays(30),
            "Honua Test Operator",
            "lic-test-338",
            DateTimeOffset.UtcNow.AddDays(-1),
            catalogEntitlements,
            activeKeys,
            SnapshotVersion: 1,
            KeyId: KeyId);
    }

    public static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

internal sealed record SignedLicenseTestFile(byte[] LicenseData, string PublicKeySetting);

internal sealed class TestLicenseEntitlementService : ILicenseEntitlementService
{
    private readonly LicenseSnapshot _snapshot;

    public TestLicenseEntitlementService(
        HonuaEdition edition,
        LicenseValidationState validationState = LicenseValidationState.Valid,
        string[]? entitlements = null)
    {
        _snapshot = LicenseTestSupport.CreateSnapshot(edition, validationState, entitlements);
    }

    public LicenseSnapshot GetSnapshot() => _snapshot;

    public LicenseEntitlementDecision CheckEntitlement(string entitlementKey)
    {
        var feature = FeatureCatalog.All.FirstOrDefault(item =>
            string.Equals(item.Key, entitlementKey, StringComparison.OrdinalIgnoreCase));
        var active = _snapshot.HasEntitlement(entitlementKey);
        var upgradeMessage = active
            ? string.Empty
            : $"{feature?.DisplayName ?? entitlementKey} requires an active {feature?.MinimumEdition.ToString() ?? "paid"} entitlement. " +
                $"Current edition is {_snapshot.Edition}; install a license that includes '{entitlementKey}'.";

        return new LicenseEntitlementDecision(
            entitlementKey,
            active,
            _snapshot.Edition,
            _snapshot.ValidationState,
            feature?.MinimumEdition,
            upgradeMessage);
    }
}

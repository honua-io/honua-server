// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Licensing.Domain;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Honua.LicenseMint;

internal sealed record LicenseMintRequest
{
    public required string KeyId { get; init; }

    public required string LicenseId { get; init; }

    public required string LicensedTo { get; init; }

    public required HonuaEdition Edition { get; init; }

    public DateTimeOffset? IssuedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string[]? Entitlements { get; init; }

    public LicenseCapacityTerms? Capacity { get; init; }

    public Dictionary<string, string>? Metadata { get; init; }
}

internal sealed record LicenseMintResult(byte[] EnvelopeJson, byte[] SignedPayloadBytes);

internal static class LicenseMinter
{
    public const string PayloadSchema = "honua.license/v1";

    public const int EnvelopeVersion = 1;

    public static LicenseMintResult Mint(LicenseMintRequest request, LicenseSigningKeyPair keyPair)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(keyPair);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.KeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LicenseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LicensedTo);

        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException(
                "ExpiresAt must be in the future; an already-expired license is rejected by the runtime.",
                nameof(request));
        }

        ValidateCapacityTerms(request.Capacity);

        var entitlements = ResolveEntitlements(request);

        var payload = new LicenseMintPayload
        {
            Schema = PayloadSchema,
            LicenseId = request.LicenseId,
            LicensedTo = request.LicensedTo,
            Edition = request.Edition.ToString(),
            IssuedAt = request.IssuedAt ?? DateTimeOffset.UtcNow,
            ExpiresAt = request.ExpiresAt,
            Entitlements = entitlements,
            Capacity = request.Capacity,
            Metadata = request.Metadata is { Count: > 0 } ? request.Metadata : null
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            LicenseMintJsonContext.Default.LicenseMintPayload);

        var signature = Sign(payloadBytes, keyPair.PrivateSeed);

        var envelope = new LicenseMintEnvelope
        {
            Version = EnvelopeVersion,
            KeyId = request.KeyId,
            Payload = Base64Url.Encode(payloadBytes),
            Signature = Base64Url.Encode(signature)
        };

        var envelopeJson = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            LicenseMintJsonContext.Default.LicenseMintEnvelope);

        return new LicenseMintResult(envelopeJson, payloadBytes);
    }

    private static string[] ResolveEntitlements(LicenseMintRequest request)
    {
        if (request.Entitlements is null)
        {
            return FeatureCatalog.All
                .Where(feature => feature.MinimumEdition <= request.Edition)
                .Select(feature => feature.Key)
                .ToArray();
        }

        var known = FeatureCatalog.All
            .Select(feature => feature.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = request.Entitlements
            .Where(key => !known.Contains(key))
            .ToArray();

        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown entitlement key(s): {string.Join(", ", unknown)}. " +
                "Entitlements must match FeatureCatalog keys.",
                nameof(request));
        }

        return request.Entitlements
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ValidateCapacityTerms(LicenseCapacityTerms? capacity)
    {
        if (capacity is null)
        {
            return;
        }

        if (capacity.MaxSustainedServingUnits <= 0m)
        {
            throw new ArgumentException(
                "Capacity max sustained serving units must be greater than zero.",
                nameof(capacity));
        }

        if (capacity.AnnualSurgeDays is < 0)
        {
            throw new ArgumentException(
                "Capacity annual surge days cannot be negative.",
                nameof(capacity));
        }

        if (capacity.SurgeAllowance is not (
            LicenseCapacitySurgeAllowances.Standard or
            LicenseCapacitySurgeAllowances.High or
            LicenseCapacitySurgeAllowances.Unlimited))
        {
            throw new ArgumentException(
                "Capacity surge allowance must be standard, high, or unlimited.",
                nameof(capacity));
        }
    }

    private static byte[] Sign(byte[] payloadBytes, byte[] privateSeed)
    {
        var privateKey = new Ed25519PrivateKeyParameters(privateSeed, 0);
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, privateKey);
        signer.BlockUpdate(payloadBytes, 0, payloadBytes.Length);
        return signer.GenerateSignature();
    }

    public static string ToPrettyEnvelopeJson(LicenseMintResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var document = JsonDocument.Parse(result.EnvelopeJson);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            document.RootElement.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string ToCompactEnvelopeJson(LicenseMintResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Encoding.UTF8.GetString(result.EnvelopeJson);
    }
}

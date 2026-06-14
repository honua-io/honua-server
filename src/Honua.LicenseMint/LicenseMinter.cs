// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Licensing.Domain;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Honua.LicenseMint;

/// <summary>
/// Parameters for minting a single signed license envelope.
/// </summary>
internal sealed record LicenseMintRequest
{
    /// <summary>Trusted signing key identifier written to the envelope and resolved at runtime.</summary>
    public required string KeyId { get; init; }

    /// <summary>Stable issuance identifier for the license.</summary>
    public required string LicenseId { get; init; }

    /// <summary>Operator / licensee display name.</summary>
    public required string LicensedTo { get; init; }

    /// <summary>Edition the license grants.</summary>
    public required HonuaEdition Edition { get; init; }

    /// <summary>Issue timestamp. Defaults to now (UTC) when minted.</summary>
    public DateTimeOffset? IssuedAt { get; init; }

    /// <summary>Optional expiry timestamp. Must be in the future to validate at runtime.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Explicit entitlement keys to activate. When null, every catalog feature whose
    /// minimum edition is at or below <see cref="Edition"/> is granted.
    /// </summary>
    public string[]? Entitlements { get; init; }

    /// <summary>Optional issuance-context metadata.</summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// The result of minting a license: the serialized envelope JSON plus the canonical
/// payload bytes that were signed.
/// </summary>
internal sealed record LicenseMintResult(byte[] EnvelopeJson, byte[] SignedPayloadBytes);

/// <summary>
/// Mints Ed25519-signed Honua license envelopes that the runtime
/// <c>FileBackedLicenseService</c> verifier accepts.
/// </summary>
/// <remarks>
/// The signature is computed over the exact UTF-8 JSON payload bytes (ADR-0033). The
/// payload is serialized with <see cref="LicenseMintJsonContext"/>, whose naming and
/// null-omission policy mirror the runtime verifier's source-generated context, so
/// the bytes the mint signs are identical to the bytes the verifier checks.
/// </remarks>
internal static class LicenseMinter
{
    /// <summary>Current payload schema discriminator.</summary>
    public const string PayloadSchema = "honua.license/v1";

    /// <summary>Current envelope version understood by the runtime.</summary>
    public const int EnvelopeVersion = 1;

    /// <summary>
    /// Validates the request, builds the canonical payload, signs it with the supplied
    /// key pair, and returns the serialized envelope.
    /// </summary>
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

    /// <summary>
    /// Resolves the entitlement set. Unknown keys are rejected up front (the runtime
    /// silently ignores them, but minting an unknown key is always an operator error).
    /// </summary>
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

    private static byte[] Sign(byte[] payloadBytes, byte[] privateSeed)
    {
        var privateKey = new Ed25519PrivateKeyParameters(privateSeed, 0);
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, privateKey);
        signer.BlockUpdate(payloadBytes, 0, payloadBytes.Length);
        return signer.GenerateSignature();
    }

    /// <summary>
    /// Returns indented envelope JSON for human-readable file output. The runtime
    /// verifier re-decodes the embedded Base64URL payload, so file-level indentation
    /// of the envelope does not affect the signed bytes.
    /// </summary>
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

    /// <summary>Returns the canonical (compact) envelope JSON as a UTF-8 string.</summary>
    public static string ToCompactEnvelopeJson(LicenseMintResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Encoding.UTF8.GetString(result.EnvelopeJson);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.LicenseMint;

/// <summary>
/// Signed license payload that is serialized to UTF-8 JSON, signed, and embedded
/// (Base64URL-encoded) as the <c>payload</c> field of the envelope.
/// </summary>
/// <remarks>
/// This is the mint-side mirror of the runtime verifier's <c>SignedLicensePayload</c>
/// (<c>Honua.Infrastructure.Licensing</c> in <c>Honua.Hosting</c>). The field names,
/// camel-case naming policy, and null-omission must stay byte-identical to the
/// verifier's source-generated context, because the Ed25519 signature is computed
/// over the exact decoded payload bytes (ADR-0033). The
/// <c>LicenseMintRoundTripTests</c> integration test drives minted envelopes through
/// the real runtime verifier to guarantee this lockstep.
/// </remarks>
internal sealed class LicenseMintPayload
{
    /// <summary>Schema discriminator. Must be <c>honua.license/v1</c>.</summary>
    public string? Schema { get; init; }

    /// <summary>Stable issuance identifier for the license.</summary>
    public string? LicenseId { get; init; }

    /// <summary>Operator / licensee display name.</summary>
    public string? LicensedTo { get; init; }

    /// <summary>Edition label (<c>Community</c>, <c>Pro</c>, <c>Enterprise</c>).</summary>
    public string? Edition { get; init; }

    /// <summary>Issue timestamp (RFC 3339).</summary>
    public DateTimeOffset? IssuedAt { get; init; }

    /// <summary>Optional expiry timestamp (RFC 3339). When present, must be in the future.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Active feature entitlement keys resolved against <c>FeatureCatalog</c>.</summary>
    public string[]? Entitlements { get; init; }

    /// <summary>Optional string-valued metadata (issuance source, support context).</summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// The signed license envelope written to disk. Mirror of the runtime verifier's
/// <c>SignedLicenseEnvelope</c>.
/// </summary>
internal sealed class LicenseMintEnvelope
{
    /// <summary>Envelope schema version. The runtime expects <c>1</c>.</summary>
    public int Version { get; init; }

    /// <summary>Trusted signing key identifier, resolved at runtime from <c>Licensing:TrustedKeys</c>.</summary>
    public string? KeyId { get; init; }

    /// <summary>Base64URL-encoded UTF-8 JSON payload bytes.</summary>
    public string? Payload { get; init; }

    /// <summary>Base64URL-encoded Ed25519 signature over the decoded payload bytes.</summary>
    public string? Signature { get; init; }
}

/// <summary>
/// Source-generated JSON context for the mint payload and envelope. The naming
/// policy and null-omission options mirror the runtime verifier's
/// <c>LicenseFileJsonContext</c> exactly so the signed payload bytes are stable.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(LicenseMintPayload))]
[JsonSerializable(typeof(LicenseMintEnvelope))]
internal sealed partial class LicenseMintJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

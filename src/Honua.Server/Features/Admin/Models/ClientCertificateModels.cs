// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Client-certificate trust profile returned by the admin security API.
/// </summary>
public sealed class ClientCertificateTrustProfileResponse
{
    /// <summary>Trust profile identifier.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Honua environment identifier this profile applies to.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>Operator-facing display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Whether the profile can authenticate certificates.</summary>
    public bool Enabled { get; init; }

    /// <summary>Accepted issuer certificate thumbprints.</summary>
    public string[] AcceptedIssuerThumbprints { get; init; } = [];

    /// <summary>Accepted issuer distinguished names.</summary>
    public string[] AcceptedIssuerSubjects { get; init; } = [];

    /// <summary>Allowed SAN identity types.</summary>
    public string[] AllowedSanTypes { get; init; } = [];

    /// <summary>Whether subject distinguished-name fallback is enabled.</summary>
    public bool AllowSubjectFallback { get; init; }

    /// <summary>Whether the Client Authentication EKU is required.</summary>
    public bool RequireClientAuthenticationEku { get; init; }

    /// <summary>Whether platform/custom-root chain trust is required.</summary>
    public bool RequireChainTrust { get; init; }

    /// <summary>Chain revocation mode used when chain trust is enabled.</summary>
    public required string ChainRevocationMode { get; init; }

    /// <summary>Days before expiry when warning audit events are emitted.</summary>
    public int ExpirationWarningThresholdDays { get; init; }

    /// <summary>Operator-defined rotation grace window in days.</summary>
    public int RotationGracePeriodDays { get; init; }

    /// <summary>Monotonic profile revision.</summary>
    public long Revision { get; init; }

    /// <summary>Configured principal mappings.</summary>
    public ClientCertificatePrincipalMappingResponse[] PrincipalMappings { get; init; } = [];

    /// <summary>Configured revocation entries.</summary>
    public ClientCertificateRevocationEntryResponse[] Revocations { get; init; } = [];
}

/// <summary>
/// Request to create or update client-certificate trust profile metadata.
/// </summary>
public sealed class UpsertClientCertificateTrustProfileRequest
{
    /// <summary>Optional trust profile id for create requests.</summary>
    public string? ProfileId { get; init; }

    /// <summary>Honua environment identifier this profile applies to.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>Operator-facing display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Whether the profile can authenticate certificates.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Accepted issuer certificate thumbprints.</summary>
    public string[] AcceptedIssuerThumbprints { get; init; } = [];

    /// <summary>Accepted issuer distinguished names.</summary>
    public string[] AcceptedIssuerSubjects { get; init; } = [];

    /// <summary>Allowed SAN identity types: fingerprintSha256, sanUri, sanEmail, sanDns, subject.</summary>
    public string[] AllowedSanTypes { get; init; } = ["sanUri", "sanEmail", "sanDns"];

    /// <summary>Whether subject distinguished-name fallback is enabled.</summary>
    public bool AllowSubjectFallback { get; init; }

    /// <summary>Whether the Client Authentication EKU is required.</summary>
    public bool RequireClientAuthenticationEku { get; init; } = true;

    /// <summary>Whether platform/custom-root chain trust is required.</summary>
    public bool RequireChainTrust { get; init; }

    /// <summary>Chain revocation mode used when chain trust is enabled.</summary>
    public string ChainRevocationMode { get; init; } = "NoCheck";

    /// <summary>Days before expiry when warning audit events are emitted.</summary>
    public int ExpirationWarningThresholdDays { get; init; } = 30;

    /// <summary>Operator-defined rotation grace window in days.</summary>
    public int RotationGracePeriodDays { get; init; }

    /// <summary>Optional custom trust anchor certificates as PEM or base64 DER public certificates.</summary>
    public string[] CustomTrustAnchorCertificates { get; init; } = [];
}

/// <summary>
/// Client-certificate principal mapping returned by the admin security API.
/// </summary>
public sealed class ClientCertificatePrincipalMappingResponse
{
    /// <summary>Mapping identifier.</summary>
    public required string MappingId { get; init; }

    /// <summary>Identity match type.</summary>
    public required string MatchType { get; init; }

    /// <summary>Normalized identity match value.</summary>
    public required string MatchValue { get; init; }

    /// <summary>Stable Honua principal identifier.</summary>
    public required string PrincipalId { get; init; }

    /// <summary>Operator-facing principal display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>RBAC roles projected into the authenticated principal.</summary>
    public string[] Roles { get; init; } = [];

    /// <summary>Optional tenant scope claim.</summary>
    public string? TenantId { get; init; }

    /// <summary>Environment scopes projected into the authenticated principal.</summary>
    public string[] EnvironmentScopes { get; init; } = [];

    /// <summary>Whether the mapping can authenticate a principal.</summary>
    public bool Enabled { get; init; }

    /// <summary>Optional UTC validity start.</summary>
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>Optional UTC validity end.</summary>
    public DateTimeOffset? NotAfter { get; init; }

    /// <summary>Monotonic mapping revision.</summary>
    public long Revision { get; init; }
}

/// <summary>
/// Request to create or update a client-certificate principal mapping.
/// </summary>
public sealed class UpsertClientCertificatePrincipalMappingRequest
{
    /// <summary>Optional mapping id for create requests.</summary>
    public string? MappingId { get; init; }

    /// <summary>Identity match type: fingerprintSha256, sanUri, sanEmail, sanDns, or subject.</summary>
    public required string MatchType { get; init; }

    /// <summary>Identity match value.</summary>
    public required string MatchValue { get; init; }

    /// <summary>Stable Honua principal identifier.</summary>
    public required string PrincipalId { get; init; }

    /// <summary>Operator-facing principal display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>RBAC roles projected into the authenticated principal.</summary>
    public string[] Roles { get; init; } = [];

    /// <summary>Optional tenant scope claim.</summary>
    public string? TenantId { get; init; }

    /// <summary>Environment scopes projected into the authenticated principal.</summary>
    public string[] EnvironmentScopes { get; init; } = [];

    /// <summary>Whether the mapping can authenticate a principal.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Optional UTC validity start.</summary>
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>Optional UTC validity end.</summary>
    public DateTimeOffset? NotAfter { get; init; }
}

/// <summary>
/// Client-certificate revocation entry returned by the admin security API.
/// </summary>
public sealed class ClientCertificateRevocationEntryResponse
{
    /// <summary>Revocation identifier.</summary>
    public required string RevocationId { get; init; }

    /// <summary>Revoked certificate SHA-256 fingerprint, when configured.</summary>
    public string? FingerprintSha256 { get; init; }

    /// <summary>Revoked issuer distinguished name, when configured with serial number.</summary>
    public string? Issuer { get; init; }

    /// <summary>Revoked certificate serial number, when configured with issuer.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>Operator-supplied revocation reason.</summary>
    public required string Reason { get; init; }

    /// <summary>UTC timestamp when the revocation was recorded.</summary>
    public DateTimeOffset RevokedAt { get; init; }

    /// <summary>Actor that recorded the revocation.</summary>
    public required string Actor { get; init; }

    /// <summary>Whether the revocation entry is active.</summary>
    public bool Enabled { get; init; }
}

/// <summary>
/// Request to add a client-certificate revocation entry.
/// </summary>
public sealed class AddClientCertificateRevocationRequest
{
    /// <summary>Optional revocation identifier for idempotent automation.</summary>
    public string? RevocationId { get; init; }

    /// <summary>Revoked certificate SHA-256 fingerprint.</summary>
    public string? FingerprintSha256 { get; init; }

    /// <summary>Revoked issuer distinguished name, used with serial number.</summary>
    public string? Issuer { get; init; }

    /// <summary>Revoked certificate serial number, used with issuer.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>Operator-supplied revocation reason.</summary>
    public string Reason { get; init; } = "unspecified";
}

/// <summary>
/// Request to validate a public client certificate without storing it.
/// </summary>
public sealed class ValidateClientCertificateRequest
{
    /// <summary>PEM or base64 DER public client certificate.</summary>
    public required string Certificate { get; init; }

    /// <summary>Certificate encoding: pem, urlEncodedPem, or base64Der.</summary>
    public string Encoding { get; init; } = "pem";

    /// <summary>Optional expected trust profile id.</summary>
    public string? ProfileId { get; init; }
}

/// <summary>
/// Result of validating a public client certificate against configured trust profiles.
/// </summary>
public sealed class ValidateClientCertificateResponse
{
    /// <summary>Whether validation succeeded.</summary>
    public bool Valid { get; init; }

    /// <summary>Stable machine-readable validation code.</summary>
    public required string Code { get; init; }

    /// <summary>Sanitized validation detail.</summary>
    public required string Detail { get; init; }

    /// <summary>Matched Honua principal id when validation succeeds.</summary>
    public string? PrincipalId { get; init; }

    /// <summary>Matched trust profile id when available.</summary>
    public string? TrustProfileId { get; init; }

    /// <summary>Matched mapping id when available.</summary>
    public string? MappingId { get; init; }

    /// <summary>Matched Honua environment id when available.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>Certificate SHA-256 fingerprint.</summary>
    public string? FingerprintSha256 { get; init; }

    /// <summary>Days until certificate expiry when available.</summary>
    public int? DaysUntilExpiry { get; init; }
}

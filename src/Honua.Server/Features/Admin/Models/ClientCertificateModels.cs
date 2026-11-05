// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class ClientCertificateTrustProfileResponse
{
    public required string ProfileId { get; init; }

    public required string EnvironmentId { get; init; }

    public required string DisplayName { get; init; }

    public bool Enabled { get; init; }

    public string[] AcceptedIssuerThumbprints { get; init; } = [];

    public string[] AcceptedIssuerSubjects { get; init; } = [];

    public string[] AllowedSanTypes { get; init; } = [];

    public bool AllowSubjectFallback { get; init; }

    public bool RequireClientAuthenticationEku { get; init; }

    public bool RequireChainTrust { get; init; }

    public required string ChainRevocationMode { get; init; }

    public bool RevocationStatusUnknownIsFatal { get; init; } = true;

    public int ExpirationWarningThresholdDays { get; init; }

    public int RotationGracePeriodDays { get; init; }

    public long Revision { get; init; }

    public ClientCertificatePrincipalMappingResponse[] PrincipalMappings { get; init; } = [];

    public ClientCertificateRevocationEntryResponse[] Revocations { get; init; } = [];
}

public sealed class UpsertClientCertificateTrustProfileRequest
{
    public string? ProfileId { get; init; }

    public required string EnvironmentId { get; init; }

    public string? DisplayName { get; init; }

    public bool Enabled { get; init; } = true;

    public string[] AcceptedIssuerThumbprints { get; init; } = [];

    public string[] AcceptedIssuerSubjects { get; init; } = [];

    public string[] AllowedSanTypes { get; init; } = ["sanUri", "sanEmail", "sanDns"];

    public bool AllowSubjectFallback { get; init; }

    public bool RequireClientAuthenticationEku { get; init; } = true;

    public bool RequireChainTrust { get; init; }

    public string ChainRevocationMode { get; init; } = "NoCheck";

    public bool RevocationStatusUnknownIsFatal { get; init; } = true;

    public int ExpirationWarningThresholdDays { get; init; } = 30;

    public int RotationGracePeriodDays { get; init; }

    public string[] CustomTrustAnchorCertificates { get; init; } = [];
}

public sealed class ClientCertificatePrincipalMappingResponse
{
    public required string MappingId { get; init; }

    public required string MatchType { get; init; }

    public required string MatchValue { get; init; }

    public required string PrincipalId { get; init; }

    public required string DisplayName { get; init; }

    public string[] Roles { get; init; } = [];

    public string? TenantId { get; init; }

    public string[] EnvironmentScopes { get; init; } = [];

    public bool Enabled { get; init; }

    public DateTimeOffset? NotBefore { get; init; }

    public DateTimeOffset? NotAfter { get; init; }

    public long Revision { get; init; }
}

public sealed class UpsertClientCertificatePrincipalMappingRequest
{
    public string? MappingId { get; init; }

    public required string MatchType { get; init; }

    public required string MatchValue { get; init; }

    public required string PrincipalId { get; init; }

    public string? DisplayName { get; init; }

    public string[] Roles { get; init; } = [];

    public string? TenantId { get; init; }

    public string[] EnvironmentScopes { get; init; } = [];

    public bool Enabled { get; init; } = true;

    public DateTimeOffset? NotBefore { get; init; }

    public DateTimeOffset? NotAfter { get; init; }
}

public sealed class ClientCertificateRevocationEntryResponse
{
    public required string RevocationId { get; init; }

    public string? FingerprintSha256 { get; init; }

    public string? Issuer { get; init; }

    public string? SerialNumber { get; init; }

    public required string Reason { get; init; }

    public DateTimeOffset RevokedAt { get; init; }

    public required string Actor { get; init; }

    public bool Enabled { get; init; }
}

public sealed class AddClientCertificateRevocationRequest
{
    public string? RevocationId { get; init; }

    public string? FingerprintSha256 { get; init; }

    public string? Issuer { get; init; }

    public string? SerialNumber { get; init; }

    public string Reason { get; init; } = "unspecified";
}

public sealed class ValidateClientCertificateRequest
{
    public required string Certificate { get; init; }

    public string Encoding { get; init; } = "pem";

    public string? ProfileId { get; init; }
}

public sealed class ValidateClientCertificateResponse
{
    public bool Valid { get; init; }

    public required string Code { get; init; }

    public required string Detail { get; init; }

    public string? PrincipalId { get; init; }

    public string? TrustProfileId { get; init; }

    public string? MappingId { get; init; }

    public string? EnvironmentId { get; init; }

    public string? FingerprintSha256 { get; init; }

    public int? DaysUntilExpiry { get; init; }
}

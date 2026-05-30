// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography.X509Certificates;

namespace Honua.Infrastructure.Authentication.ClientCertificates;

internal sealed class ClientCertificateAuthenticationOptions
{
    public const string SectionName = "Authentication:ClientCertificates";

    public ClientCertificateAuthenticationMode Mode { get; set; } = ClientCertificateAuthenticationMode.Disabled;

    public string EnvironmentId { get; set; } = "default";

    public string[] ProtectedAdminPathPrefixes { get; set; } = ["/api/v1/admin"];

    public string[] ProtectedGrpcServices { get; set; } =
    [
        "geospatial.v1.FeatureService",
        "geospatial.v1.ProcessService",
        "geospatial.v1.SpecService"
    ];

    public ForwardedClientCertificateOptions ForwardedCertificate { get; set; } = new();

    public ClientCertificateTrustProfileOptions[] TrustProfiles { get; set; } = [];
}

internal enum ClientCertificateAuthenticationMode
{
    Disabled = 0,
    Optional = 1,
    RequiredForNative = 2,
    RequiredForAdmin = 3,
    RequiredForEnvironment = 4,
}

internal sealed class ForwardedClientCertificateOptions
{
    public bool Enabled { get; set; }

    public string HeaderName { get; set; } = "X-Forwarded-Client-Cert";

    public ForwardedClientCertificateEncoding Encoding { get; set; } =
        ForwardedClientCertificateEncoding.UrlEncodedPem;

    public string[] TrustedProxyNetworks { get; set; } = [];
}

internal enum ForwardedClientCertificateEncoding
{
    UrlEncodedPem = 0,
    Pem = 1,
    Base64Der = 2,
}

internal sealed class ClientCertificateTrustProfileOptions
{
    public string ProfileId { get; set; } = string.Empty;

    public string EnvironmentId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string[] AcceptedIssuerThumbprints { get; set; } = [];

    public string[] AcceptedIssuerSubjects { get; set; } = [];

    public string[] CustomTrustAnchorCertificates { get; set; } = [];

    public ClientCertificateIdentityType[] AllowedSanTypes { get; set; } =
    [
        ClientCertificateIdentityType.SanUri,
        ClientCertificateIdentityType.SanEmail,
        ClientCertificateIdentityType.SanDns
    ];

    public bool AllowSubjectFallback { get; set; }

    public bool RequireClientAuthenticationEku { get; set; } = true;

    public bool RequireChainTrust { get; set; }

    public X509RevocationMode ChainRevocationMode { get; set; } = X509RevocationMode.NoCheck;

    public int ExpirationWarningThresholdDays { get; set; } = 30;

    public int RotationGracePeriodDays { get; set; }

    public long Revision { get; set; } = 1;

    public ClientCertificatePrincipalMappingOptions[] PrincipalMappings { get; set; } = [];

    public ClientCertificateRevocationEntryOptions[] Revocations { get; set; } = [];
}

internal enum ClientCertificateIdentityType
{
    FingerprintSha256 = 0,
    SanUri = 1,
    SanEmail = 2,
    SanDns = 3,
    Subject = 4,
}

internal sealed class ClientCertificatePrincipalMappingOptions
{
    public string MappingId { get; set; } = string.Empty;

    public ClientCertificateIdentityType MatchType { get; set; } = ClientCertificateIdentityType.FingerprintSha256;

    public string MatchValue { get; set; } = string.Empty;

    public string PrincipalId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string[] Roles { get; set; } = [];

    public string? TenantId { get; set; }

    public string[] EnvironmentScopes { get; set; } = [];

    public bool Enabled { get; set; } = true;

    public DateTimeOffset? NotBefore { get; set; }

    public DateTimeOffset? NotAfter { get; set; }

    public long Revision { get; set; } = 1;
}

internal sealed class ClientCertificateRevocationEntryOptions
{
    public string RevocationId { get; set; } = string.Empty;

    public string? FingerprintSha256 { get; set; }

    public string? Issuer { get; set; }

    public string? SerialNumber { get; set; }

    public string Reason { get; set; } = "unspecified";

    public DateTimeOffset RevokedAt { get; set; } = DateTimeOffset.UtcNow;

    public string Actor { get; set; } = "system";

    public bool Enabled { get; set; } = true;
}

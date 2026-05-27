// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography.X509Certificates;

namespace Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;

internal sealed record ClientCertificateTrustProfile
{
    public required string ProfileId { get; init; }

    public required string EnvironmentId { get; init; }

    public required string DisplayName { get; init; }

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<string> AcceptedIssuerThumbprints { get; init; } = [];

    public IReadOnlyList<string> AcceptedIssuerSubjects { get; init; } = [];

    public IReadOnlyList<string> CustomTrustAnchorCertificates { get; init; } = [];

    public IReadOnlyList<ClientCertificateIdentityType> AllowedSanTypes { get; init; } = [];

    public bool AllowSubjectFallback { get; init; }

    public bool RequireClientAuthenticationEku { get; init; } = true;

    public bool RequireChainTrust { get; init; }

    public X509RevocationMode ChainRevocationMode { get; init; } = X509RevocationMode.NoCheck;

    public int ExpirationWarningThresholdDays { get; init; } = 30;

    public int RotationGracePeriodDays { get; init; }

    public long Revision { get; init; } = 1;

    public IReadOnlyList<ClientCertificatePrincipalMapping> PrincipalMappings { get; init; } = [];

    public IReadOnlyList<ClientCertificateRevocationEntry> Revocations { get; init; } = [];

    public static ClientCertificateTrustProfile FromOptions(ClientCertificateTrustProfileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new ClientCertificateTrustProfile
        {
            ProfileId = options.ProfileId.Trim(),
            EnvironmentId = options.EnvironmentId.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(options.DisplayName)
                ? options.ProfileId.Trim()
                : options.DisplayName.Trim(),
            Enabled = options.Enabled,
            AcceptedIssuerThumbprints = NormalizeMany(options.AcceptedIssuerThumbprints),
            AcceptedIssuerSubjects = NormalizeMany(options.AcceptedIssuerSubjects),
            CustomTrustAnchorCertificates = options.CustomTrustAnchorCertificates
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .ToArray(),
            AllowedSanTypes = options.AllowedSanTypes.Length == 0
                ? [ClientCertificateIdentityType.SanUri, ClientCertificateIdentityType.SanEmail, ClientCertificateIdentityType.SanDns]
                : options.AllowedSanTypes,
            AllowSubjectFallback = options.AllowSubjectFallback,
            RequireClientAuthenticationEku = options.RequireClientAuthenticationEku,
            RequireChainTrust = options.RequireChainTrust,
            ChainRevocationMode = options.ChainRevocationMode,
            ExpirationWarningThresholdDays = options.ExpirationWarningThresholdDays,
            RotationGracePeriodDays = options.RotationGracePeriodDays,
            Revision = Math.Max(1, options.Revision),
            PrincipalMappings = options.PrincipalMappings
                .Select(ClientCertificatePrincipalMapping.FromOptions)
                .ToArray(),
            Revocations = options.Revocations
                .Select(ClientCertificateRevocationEntry.FromOptions)
                .ToArray(),
        };
    }

    private static string[] NormalizeMany(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => ClientCertificateNormalization.NormalizeThumbprintOrName(value))
            .ToArray();
}

internal sealed record ClientCertificatePrincipalMapping
{
    public required string MappingId { get; init; }

    public required ClientCertificateIdentityType MatchType { get; init; }

    public required string MatchValue { get; init; }

    public required string PrincipalId { get; init; }

    public required string DisplayName { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = [];

    public string? TenantId { get; init; }

    public IReadOnlyList<string> EnvironmentScopes { get; init; } = [];

    public bool Enabled { get; init; } = true;

    public DateTimeOffset? NotBefore { get; init; }

    public DateTimeOffset? NotAfter { get; init; }

    public long Revision { get; init; } = 1;

    public static ClientCertificatePrincipalMapping FromOptions(ClientCertificatePrincipalMappingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var principalId = options.PrincipalId.Trim();
        return new ClientCertificatePrincipalMapping
        {
            MappingId = string.IsNullOrWhiteSpace(options.MappingId)
                ? Guid.NewGuid().ToString("N")
                : options.MappingId.Trim(),
            MatchType = options.MatchType,
            MatchValue = ClientCertificateNormalization.NormalizeMatchValue(options.MatchType, options.MatchValue),
            PrincipalId = principalId,
            DisplayName = string.IsNullOrWhiteSpace(options.DisplayName) ? principalId : options.DisplayName.Trim(),
            Roles = NormalizeRoles(options.Roles),
            TenantId = string.IsNullOrWhiteSpace(options.TenantId) ? null : options.TenantId.Trim(),
            EnvironmentScopes = options.EnvironmentScopes
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .ToArray(),
            Enabled = options.Enabled,
            NotBefore = options.NotBefore,
            NotAfter = options.NotAfter,
            Revision = Math.Max(1, options.Revision),
        };
    }

    private static string[] NormalizeRoles(IEnumerable<string> roles)
        => roles
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .Select(static role => role.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

internal sealed record ClientCertificateRevocationEntry
{
    public required string RevocationId { get; init; }

    public string? FingerprintSha256 { get; init; }

    public string? Issuer { get; init; }

    public string? SerialNumber { get; init; }

    public required string Reason { get; init; }

    public DateTimeOffset RevokedAt { get; init; }

    public required string Actor { get; init; }

    public bool Enabled { get; init; } = true;

    public static ClientCertificateRevocationEntry FromOptions(ClientCertificateRevocationEntryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new ClientCertificateRevocationEntry
        {
            RevocationId = string.IsNullOrWhiteSpace(options.RevocationId)
                ? Guid.NewGuid().ToString("N")
                : options.RevocationId.Trim(),
            FingerprintSha256 = string.IsNullOrWhiteSpace(options.FingerprintSha256)
                ? null
                : ClientCertificateNormalization.NormalizeThumbprintOrName(options.FingerprintSha256),
            Issuer = string.IsNullOrWhiteSpace(options.Issuer)
                ? null
                : ClientCertificateNormalization.NormalizeThumbprintOrName(options.Issuer),
            SerialNumber = string.IsNullOrWhiteSpace(options.SerialNumber)
                ? null
                : ClientCertificateNormalization.NormalizeSerialNumber(options.SerialNumber),
            Reason = string.IsNullOrWhiteSpace(options.Reason) ? "unspecified" : options.Reason.Trim(),
            RevokedAt = options.RevokedAt,
            Actor = string.IsNullOrWhiteSpace(options.Actor) ? "system" : options.Actor.Trim(),
            Enabled = options.Enabled,
        };
    }
}

internal static class ClientCertificateNormalization
{
    public static string NormalizeMatchValue(ClientCertificateIdentityType matchType, string value)
    {
        var trimmed = value.Trim();
        return matchType switch
        {
            ClientCertificateIdentityType.FingerprintSha256 => NormalizeThumbprintOrName(trimmed),
            ClientCertificateIdentityType.SanEmail => trimmed.ToUpperInvariant(),
            ClientCertificateIdentityType.SanDns => trimmed.ToUpperInvariant(),
            ClientCertificateIdentityType.SanUri => trimmed,
            ClientCertificateIdentityType.Subject => NormalizeThumbprintOrName(trimmed),
            _ => trimmed,
        };
    }

    public static string NormalizeThumbprintOrName(string value)
        => value.Replace(" ", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();

    public static string NormalizeSerialNumber(string value)
        => value.Replace(" ", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
}

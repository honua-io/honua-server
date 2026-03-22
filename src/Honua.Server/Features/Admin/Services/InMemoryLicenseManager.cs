// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// In-memory license manager for the admin API surface.
/// Will be replaced by a persistent implementation when #338 lands.
/// </summary>
internal sealed class InMemoryLicenseManager : ILicenseManager
{
    private static readonly IReadOnlyList<Entitlement> _allEntitlements =
    [
        new() { Key = "oidc", Name = "OIDC Authentication", IsActive = false },
        new() { Key = "rbac", Name = "Role-Based Access Control", IsActive = false },
        new() { Key = "rate-limiting", Name = "Rate Limiting", IsActive = false },
        new() { Key = "scim", Name = "SCIM Provisioning", IsActive = false },
        new() { Key = "audit-log", Name = "Audit Logging", IsActive = false },
        new() { Key = "multi-tenant", Name = "Multi-Tenant Isolation", IsActive = false },
    ];

    private LicenseInfo _current = new()
    {
        Edition = "Community",
        IsValid = true,
        ValidationState = "Valid",
        Entitlements = _allEntitlements,
    };

    public Task<LicenseInfo> GetLicenseInfoAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_current);

    public Task<IReadOnlyList<Entitlement>> GetEntitlementsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_current.Entitlements);

    public Task<LicenseInfo> ApplyLicenseAsync(byte[] licenseData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(licenseData);

        if (licenseData.Length == 0)
        {
            throw new ArgumentException("License data cannot be empty.", nameof(licenseData));
        }

        // Placeholder: a real implementation would validate the license signature,
        // parse the license file, and update entitlements accordingly.
        _current = new LicenseInfo
        {
            Edition = "Enterprise",
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            IsValid = true,
            ValidationState = "Valid",
            LicensedTo = "Uploaded License",
            IssuedAt = DateTimeOffset.UtcNow,
            Entitlements = _allEntitlements.Select(e => new Entitlement
            {
                Key = e.Key,
                Name = e.Name,
                IsActive = true,
            }).ToList(),
        };

        return Task.FromResult(_current);
    }
}

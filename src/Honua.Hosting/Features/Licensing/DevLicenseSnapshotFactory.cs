// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Infrastructure.Licensing;

/// <summary>
/// Builds the in-memory <see cref="LicenseSnapshot"/> used by the test/dev-only
/// <c>Licensing:DevGrantEdition</c> override. Centralising the snapshot construction keeps the
/// runtime entitlement path (<see cref="DevLicenseEntitlementService"/>) and the bootstrap
/// entitlement probe (<see cref="FileBackedLicenseService.LoadBootstrapSnapshotAsync"/>)
/// consistent — both grant every catalog entitlement up to the configured edition, so a startup
/// gate cannot disagree with a runtime gate (honua-server#1787).
/// </summary>
internal static class DevLicenseSnapshotFactory
{
    /// <summary>
    /// Builds a dev-grant snapshot granting every catalog entitlement up to
    /// <paramref name="grantEdition"/>.
    /// </summary>
    /// <param name="grantEdition">The highest edition whose entitlements are granted.</param>
    /// <returns>An in-memory snapshot reflecting the dev grant.</returns>
    public static LicenseSnapshot Create(HonuaEdition grantEdition)
    {
        var activeKeys = FeatureCatalog.All
            .Where(feature => feature.MinimumEdition <= grantEdition)
            .Select(feature => feature.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entitlements = FeatureCatalog.All
            .Select(feature => new Entitlement
            {
                Key = feature.Key,
                Name = feature.DisplayName,
                IsActive = activeKeys.Contains(feature.Key),
            })
            .ToArray();

        return new LicenseSnapshot(
            grantEdition,
            IsValid: true,
            LicenseValidationState.Valid,
            ExpiresAt: null,
            LicensedTo: "Honua Dev Grant",
            LicenseId: "dev-grant",
            IssuedAt: null,
            entitlements,
            activeKeys,
            SnapshotVersion: 1,
            KeyId: null);
    }

    /// <summary>
    /// Attempts to parse a configured <c>Licensing:DevGrantEdition</c> value into a
    /// <see cref="HonuaEdition"/>.
    /// </summary>
    /// <param name="devGrantEdition">The raw configuration value (may be null/empty).</param>
    /// <param name="edition">The parsed edition when the value is a valid edition name.</param>
    /// <returns><see langword="true"/> when a non-empty value parsed to a known edition.</returns>
    public static bool TryParseEdition(string? devGrantEdition, out HonuaEdition edition)
    {
        if (!string.IsNullOrWhiteSpace(devGrantEdition) &&
            Enum.TryParse(devGrantEdition, ignoreCase: true, out edition))
        {
            return true;
        }

        edition = default;
        return false;
    }
}

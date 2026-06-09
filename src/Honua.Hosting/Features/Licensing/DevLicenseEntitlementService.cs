// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.Licensing;

/// <summary>
/// Test/dev-only <see cref="ILicenseEntitlementService"/> that grants every catalog entitlement up
/// to a configured edition without a signed license. Activated only when
/// <c>Licensing:DevGrantEdition</c> is set (default off). It exists so an out-of-process test/CI
/// server can exercise edition-gated features — chiefly feature editing (<c>editing.feature-edits</c>,
/// honua-server#1548/#1577) — that in-process tests grant via <c>TestLicenseEntitlementService</c>.
/// Never enable this in production.
/// </summary>
internal sealed partial class DevLicenseEntitlementService : ILicenseEntitlementService
{
    private readonly LicenseSnapshot _snapshot;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Licensing:DevGrantEdition is active ({Edition}): all entitlements up to that edition are " +
            "granted WITHOUT a signed license. This is a test/dev-only override and must never be used in production.")]
    private static partial void LogDevGrantActive(ILogger logger, HonuaEdition edition);

    /// <summary>
    /// Initializes a new instance of the <see cref="DevLicenseEntitlementService"/> class.
    /// </summary>
    /// <param name="grantEdition">The highest edition whose entitlements are granted.</param>
    /// <param name="logger">Optional logger used to warn that the override is active.</param>
    public DevLicenseEntitlementService(
        HonuaEdition grantEdition,
        ILogger<DevLicenseEntitlementService>? logger = null)
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

        _snapshot = new LicenseSnapshot(
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

        if (logger is not null)
        {
            LogDevGrantActive(logger, grantEdition);
        }
    }

    /// <inheritdoc />
    public LicenseSnapshot GetSnapshot() => _snapshot;

    /// <inheritdoc />
    public LicenseEntitlementDecision CheckEntitlement(string entitlementKey)
    {
        var feature = FeatureCatalog.All.FirstOrDefault(item =>
            string.Equals(item.Key, entitlementKey, StringComparison.OrdinalIgnoreCase));
        var active = _snapshot.HasEntitlement(entitlementKey);
        var message = active
            ? string.Empty
            : $"{feature?.DisplayName ?? entitlementKey} requires {feature?.MinimumEdition.ToString() ?? "a paid"} " +
                $"edition; dev grant edition is {_snapshot.Edition}.";

        return new LicenseEntitlementDecision(
            entitlementKey,
            active,
            _snapshot.Edition,
            _snapshot.ValidationState,
            feature?.MinimumEdition,
            message);
    }
}

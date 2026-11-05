// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.Licensing;

// This grant exists only for out-of-process tests and must never be enabled in production.
internal sealed partial class DevLicenseEntitlementService : ILicenseEntitlementService
{
    private readonly LicenseSnapshot _snapshot;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Licensing:DevGrantEdition is active ({Edition}): all entitlements up to that edition are " +
            "granted WITHOUT a signed license. This is a test/dev-only override and must never be used in production.")]
    private static partial void LogDevGrantActive(ILogger logger, HonuaEdition edition);

    public DevLicenseEntitlementService(
        HonuaEdition grantEdition,
        ILogger<DevLicenseEntitlementService>? logger = null)
    {
        _snapshot = DevLicenseSnapshotFactory.Create(grantEdition);

        if (logger is not null)
        {
            LogDevGrantActive(logger, grantEdition);
        }
    }

    public LicenseSnapshot GetSnapshot() => _snapshot;

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

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Infrastructure.Licensing;

internal static class DevLicenseSnapshotFactory
{
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

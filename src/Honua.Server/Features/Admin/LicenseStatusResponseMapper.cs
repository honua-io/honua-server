// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

internal static class LicenseStatusResponseMapper
{
    internal static LicenseStatusResponse FromInfo(LicenseInfo info, int expiryWarningDays)
    {
        ArgumentNullException.ThrowIfNull(info);

        var daysUntilExpiry = GetDaysUntilExpiry(info.ExpiresAt);

        return new LicenseStatusResponse
        {
            Edition = info.Edition,
            ExpiresAt = info.ExpiresAt,
            IsValid = info.IsValid,
            ValidationState = info.ValidationState,
            LicensedTo = info.LicensedTo,
            LicenseId = info.LicenseId,
            IssuedAt = info.IssuedAt,
            DaysUntilExpiry = daysUntilExpiry,
            ExpiryWarning = IsExpiryWarning(daysUntilExpiry, expiryWarningDays),
            Entitlements = info.Entitlements.Select(ToEntitlementResponse).ToList(),
        };
    }

    internal static LicenseStatusResponse FromStatus(LicenseStatus status, int expiryWarningDays)
    {
        ArgumentNullException.ThrowIfNull(status);

        var daysUntilExpiry = status.DaysUntilExpiry;

        return new LicenseStatusResponse
        {
            Edition = status.Edition.ToString(),
            ExpiresAt = status.ExpiresAt,
            IsValid = status.IsValid,
            ValidationState = status.ValidationState.ToString(),
            LicensedTo = status.LicensedTo,
            LicenseId = status.LicenseId,
            IssuedAt = status.IssuedAt,
            DaysUntilExpiry = daysUntilExpiry,
            ExpiryWarning = IsExpiryWarning(daysUntilExpiry, expiryWarningDays),
            Entitlements = (status.Entitlements ?? []).Select(ToEntitlementResponse).ToList(),
        };
    }

    private static EntitlementResponse ToEntitlementResponse(Entitlement entitlement)
        => new()
        {
            Key = entitlement.Key,
            Name = entitlement.Name,
            IsActive = entitlement.IsActive,
        };

    private static int? GetDaysUntilExpiry(DateTimeOffset? expiresAt)
        => expiresAt.HasValue
            ? (int)Math.Ceiling((expiresAt.Value - DateTimeOffset.UtcNow).TotalDays)
            : null;

    private static bool IsExpiryWarning(int? daysUntilExpiry, int expiryWarningDays)
        => daysUntilExpiry.HasValue && daysUntilExpiry.Value <= expiryWarningDays;
}

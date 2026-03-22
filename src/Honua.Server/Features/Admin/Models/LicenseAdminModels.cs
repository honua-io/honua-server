// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response model for license status information.
/// </summary>
public sealed class LicenseStatusResponse
{
    /// <summary>
    /// Active platform edition name.
    /// </summary>
    public required string Edition { get; init; }

    /// <summary>
    /// Whether the license is currently valid.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// License expiry date, null for perpetual/community.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Licensee name.
    /// </summary>
    public string? LicensedTo { get; init; }

    /// <summary>
    /// Days until license expiry, null if no expiry.
    /// </summary>
    public int? DaysUntilExpiry { get; init; }

    /// <summary>
    /// Whether the license is within the expiry warning threshold (30 days or less).
    /// </summary>
    public bool ExpiryWarning { get; init; }
}

/// <summary>
/// Response model for license entitlements.
/// </summary>
public sealed class LicenseEntitlementsResponse
{
    /// <summary>
    /// Active platform edition name.
    /// </summary>
    public required string Edition { get; init; }

    /// <summary>
    /// List of feature entitlements.
    /// </summary>
    public required FeatureEntitlementItem[] Features { get; init; }
}

/// <summary>
/// Individual feature entitlement status.
/// </summary>
public sealed class FeatureEntitlementItem
{
    /// <summary>
    /// Unique feature key.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Feature category for grouping.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Whether the feature is enabled under the current license.
    /// </summary>
    public required bool IsEnabled { get; init; }

    /// <summary>
    /// Minimum edition required to enable this feature.
    /// </summary>
    public required string MinimumEdition { get; init; }

    /// <summary>
    /// Whether an upgrade is required to enable this feature.
    /// </summary>
    public required bool UpgradeRequired { get; init; }
}

/// <summary>
/// Response model for license upload result.
/// </summary>
public sealed class LicenseUploadResponse
{
    /// <summary>
    /// Whether the upload was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Descriptive message about the result.
    /// </summary>
    public required string Message { get; init; }
}

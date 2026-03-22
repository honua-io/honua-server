// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Domain;

/// <summary>
/// Honua platform edition levels for feature gating.
/// </summary>
public enum HonuaEdition
{
    /// <summary>
    /// Community edition — open-source baseline features.
    /// </summary>
    Community = 0,

    /// <summary>
    /// Pro edition — advanced alerting, geocoding, and analytics.
    /// </summary>
    Pro = 1,

    /// <summary>
    /// Enterprise edition — full platform with OIDC, all channels, and advanced triggers.
    /// </summary>
    Enterprise = 2,
}

/// <summary>
/// Current license status including edition, validity, and expiry information.
/// </summary>
/// <param name="Edition">Active platform edition</param>
/// <param name="IsValid">Whether the license is valid</param>
/// <param name="ExpiresAt">License expiry date, null for perpetual/community</param>
/// <param name="LicensedTo">Licensee name</param>
public sealed record LicenseStatus(
    HonuaEdition Edition,
    bool IsValid,
    DateTimeOffset? ExpiresAt,
    string? LicensedTo)
{
    /// <summary>
    /// Days until license expiry, null if no expiry.
    /// </summary>
    public int? DaysUntilExpiry => ExpiresAt.HasValue
        ? (int)Math.Ceiling((ExpiresAt.Value - DateTimeOffset.UtcNow).TotalDays)
        : null;
}

/// <summary>
/// Result of a license upload operation.
/// </summary>
/// <param name="Success">Whether the upload succeeded</param>
/// <param name="Message">Descriptive message about the result</param>
public sealed record LicenseUploadResult(bool Success, string Message);

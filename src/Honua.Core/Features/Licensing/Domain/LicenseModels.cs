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
/// Validation state for the active license snapshot.
/// </summary>
public enum LicenseValidationState
{
    /// <summary>
    /// No license path is configured; the server is running in Community mode.
    /// </summary>
    NoLicenseConfigured = 0,

    /// <summary>
    /// A configured license file was loaded and validated successfully.
    /// </summary>
    Valid = 1,

    /// <summary>
    /// A configured license path does not point to an existing file.
    /// </summary>
    MissingFile = 2,

    /// <summary>
    /// The license envelope, payload, or configured public key could not be parsed.
    /// </summary>
    Malformed = 3,

    /// <summary>
    /// The license references a key identifier that is not trusted by this instance.
    /// </summary>
    UnknownKey = 4,

    /// <summary>
    /// The Ed25519 signature did not verify against the trusted key.
    /// </summary>
    InvalidSignature = 5,

    /// <summary>
    /// The license expiry is in the past.
    /// </summary>
    Expired = 6,
}

/// <summary>
/// Current license status including edition, validity, and expiry information.
/// </summary>
/// <param name="Edition">Active platform edition</param>
/// <param name="IsValid">Whether the license is valid</param>
/// <param name="ExpiresAt">License expiry date, null for perpetual/community</param>
/// <param name="LicensedTo">Licensee name</param>
/// <param name="ValidationState">Validation state for the active license</param>
/// <param name="LicenseId">Stable license identifier when a license is loaded</param>
/// <param name="IssuedAt">License issue timestamp when a license is loaded</param>
/// <param name="Entitlements">Known feature entitlements and their active state</param>
public sealed record LicenseStatus(
    HonuaEdition Edition,
    bool IsValid,
    DateTimeOffset? ExpiresAt,
    string? LicensedTo,
    LicenseValidationState ValidationState = LicenseValidationState.Valid,
    string? LicenseId = null,
    DateTimeOffset? IssuedAt = null,
    IReadOnlyList<Entitlement>? Entitlements = null)
{
    /// <summary>
    /// Days until license expiry, null if no expiry.
    /// </summary>
    public int? DaysUntilExpiry => ExpiresAt.HasValue
        ? (int)Math.Ceiling((ExpiresAt.Value - DateTimeOffset.UtcNow).TotalDays)
        : null;
}

/// <summary>
/// Immutable license snapshot used by runtime entitlement gates.
/// </summary>
/// <param name="Edition">Operator-facing active edition label.</param>
/// <param name="IsValid">Whether a configured paid license validated successfully.</param>
/// <param name="ValidationState">Validation state for the current snapshot.</param>
/// <param name="ExpiresAt">License expiry date, null for Community or perpetual licenses.</param>
/// <param name="LicensedTo">Licensee name when present.</param>
/// <param name="LicenseId">Stable license identifier when present.</param>
/// <param name="IssuedAt">Issue timestamp when present.</param>
/// <param name="Entitlements">Known feature entitlement inventory with active state.</param>
/// <param name="ActiveEntitlementKeys">Active entitlement keys for O(1) gate checks.</param>
/// <param name="SnapshotVersion">Monotonic in-process snapshot version.</param>
/// <param name="KeyId">Trusted signing key identifier when a license is loaded.</param>
public sealed record LicenseSnapshot(
    HonuaEdition Edition,
    bool IsValid,
    LicenseValidationState ValidationState,
    DateTimeOffset? ExpiresAt,
    string? LicensedTo,
    string? LicenseId,
    DateTimeOffset? IssuedAt,
    IReadOnlyList<Entitlement> Entitlements,
    IReadOnlySet<string> ActiveEntitlementKeys,
    long SnapshotVersion,
    string? KeyId)
{
    /// <summary>
    /// Gets whether the supplied entitlement key is active in this snapshot.
    /// </summary>
    /// <param name="entitlementKey">Feature entitlement key.</param>
    /// <returns><see langword="true"/> when the entitlement is active.</returns>
    public bool HasEntitlement(string entitlementKey) => ActiveEntitlementKeys.Contains(entitlementKey);
}

/// <summary>
/// Entitlement decision returned by the runtime license gate.
/// </summary>
/// <param name="EntitlementKey">Feature entitlement key that was checked.</param>
/// <param name="IsActive">Whether the entitlement is active.</param>
/// <param name="Edition">Operator-facing edition from the current snapshot.</param>
/// <param name="ValidationState">Validation state from the current snapshot.</param>
/// <param name="RequiredEdition">Minimum catalog edition for the entitlement, when known.</param>
/// <param name="UpgradeMessage">Safe client-facing upgrade message when inactive.</param>
public sealed record LicenseEntitlementDecision(
    string EntitlementKey,
    bool IsActive,
    HonuaEdition Edition,
    LicenseValidationState ValidationState,
    HonuaEdition? RequiredEdition,
    string UpgradeMessage);

/// <summary>
/// Result of a license upload operation.
/// </summary>
/// <param name="Success">Whether the upload succeeded</param>
/// <param name="Message">Descriptive message about the result</param>
public sealed record LicenseUploadResult(bool Success, string Message);

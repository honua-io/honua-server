// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Domain;

public enum LicenseMode
{
    Enabled = 0,

    Disabled = 1,
}

public enum HonuaEdition
{
    Community = 0,

    Pro = 1,

    Enterprise = 2,
}

public enum LicenseValidationState
{
    NoLicenseConfigured = 0,

    Valid = 1,

    MissingFile = 2,

    Malformed = 3,

    UnknownKey = 4,

    InvalidSignature = 5,

    Expired = 6,

    Disabled = 7,
}

public sealed record LicenseStatus(
    HonuaEdition Edition,
    bool IsValid,
    DateTimeOffset? ExpiresAt,
    string? LicensedTo,
    LicenseValidationState ValidationState = LicenseValidationState.Valid,
    string? LicenseId = null,
    DateTimeOffset? IssuedAt = null,
    IReadOnlyList<Entitlement>? Entitlements = null,
    LicenseCapacityTerms? CapacityTerms = null)
{
    public LicenseMode Mode { get; init; } = LicenseMode.Enabled;

    public string EditionName => Mode == LicenseMode.Disabled ? LicenseSnapshot.DisabledEditionName : Edition.ToString();

    public int? DaysUntilExpiry => ExpiresAt.HasValue
        ? (int)Math.Ceiling((ExpiresAt.Value - DateTimeOffset.UtcNow).TotalDays)
        : null;
}

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
    string? KeyId,
    LicenseCapacityTerms? CapacityTerms = null)
{
    public const string DisabledEditionName = "Unlicensed-2026.1";

    public LicenseMode Mode { get; init; } = LicenseMode.Enabled;

    public string EditionName => Mode == LicenseMode.Disabled ? DisabledEditionName : Edition.ToString();

    public bool HasEntitlement(string entitlementKey) => ActiveEntitlementKeys.Contains(entitlementKey);
}

public sealed record LicenseEntitlementDecision(
    string EntitlementKey,
    bool IsActive,
    HonuaEdition Edition,
    LicenseValidationState ValidationState,
    HonuaEdition? RequiredEdition,
    string UpgradeMessage)
{
    public LicenseMode Mode { get; init; } = LicenseMode.Enabled;
}

public sealed record LicenseUploadResult(bool Success, string Message);

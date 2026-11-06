// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.FeatureLocks;

public readonly record struct FeatureRef(
    string ServiceName,
    int LayerId,
    string FeatureId)
{
    // The lease key normalizes service name and object id, or a claim and a write of the same feature compare unequal and the lock does nothing.
    public static FeatureRef Canonical(string serviceName, int layerId, string featureId)
    {
        ArgumentNullException.ThrowIfNull(serviceName);
        ArgumentNullException.ThrowIfNull(featureId);

        var trimmedFeatureId = featureId.Trim();
        var normalizedFeatureId = long.TryParse(
            trimmedFeatureId,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var numericFeatureId)
            ? numericFeatureId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : trimmedFeatureId;

        return new FeatureRef(
            serviceName.Trim().ToLowerInvariant(),
            layerId,
            normalizedFeatureId);
    }

    public static FeatureRef Canonical(string serviceName, int layerId, long objectId)
        => Canonical(serviceName, layerId, objectId.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

// The lease principal is server-stamped and is never taken from the client.
public sealed record LockHolder(
    string HolderId,
    string? DisplayName = null,
    string? SessionId = null,
    string? TenantId = null,
    string? PrincipalName = null);

public sealed record FeatureLockLease(
    string LockId,
    FeatureRef Feature,
    LockHolder Holder,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RenewedAt = null);

public readonly record struct FeatureLockAccessContext(
    bool IsAuthorized,
    bool CanWrite)
{
    public static FeatureLockAccessContext AuthorizedWrite => new(IsAuthorized: true, CanWrite: true);

    public static FeatureLockAccessContext Unauthorized => new(IsAuthorized: false, CanWrite: false);

    public static FeatureLockAccessContext ReadOnly => new(IsAuthorized: true, CanWrite: false);
}

public sealed record FeatureLockHeldError(
    string Code,
    string Message,
    FeatureRef Feature,
    LockHolder Holder,
    DateTimeOffset ExpiresAt)
{
    public static FeatureLockHeldError FromLease(FeatureLockLease lease)
        => new(
            "feature-lock-held",
            "The feature is locked by another editor.",
            lease.Feature,
            lease.Holder,
            lease.ExpiresAt);
}

public sealed record FeatureVersionConflictError(
    string Code,
    string Message,
    FeatureRef Feature,
    string? ExpectedVersion,
    string CurrentVersion)
{
    public const string ConflictCode = "feature-version-conflict";

    public static FeatureVersionConflictError Create(
        FeatureRef feature,
        string? expectedVersion,
        string currentVersion)
        => new(
            ConflictCode,
            "The feature changed on the server after it was loaded by the client.",
            feature,
            expectedVersion,
            currentVersion);
}

public sealed record FeatureEditConflictResponse(
    string Code,
    string Reason,
    string Message,
    string Operation,
    FeatureRef Feature,
    FeatureLockHeldError? Lock,
    FeatureVersionConflictError? Version,
    DateTimeOffset OccurredAt)
{
    public const string ConflictCode = "edit-conflict";

    public static FeatureEditConflictResponse FromLockHeld(
        string operation,
        FeatureLockHeldError lockError,
        DateTimeOffset occurredAt)
        => new(
            ConflictCode,
            lockError.Code,
            "The edit cannot be applied because the target feature is locked.",
            operation,
            lockError.Feature,
            lockError,
            Version: null,
            occurredAt);

    public static FeatureEditConflictResponse FromVersionConflict(
        string operation,
        FeatureVersionConflictError versionError,
        DateTimeOffset occurredAt)
        => new(
            ConflictCode,
            versionError.Code,
            "The edit cannot be applied because the feature changed on the server.",
            operation,
            versionError.Feature,
            Lock: null,
            versionError,
            occurredAt);
}

public enum FeatureLockClaimStatus
{
    Claimed,

    Renewed,

    HeldByOther,

    Denied
}

public sealed record FeatureLockClaimResponse(
    FeatureLockClaimStatus Status,
    FeatureLockLease? Lease = null,
    FeatureLockHeldError? Error = null,
    string? DenialReason = null)
{
    public bool IsSuccess => Status is FeatureLockClaimStatus.Claimed or FeatureLockClaimStatus.Renewed;
}

public enum FeatureLockRenewStatus
{
    Renewed,

    NotFound,

    HeldByOther,

    Denied
}

public sealed record FeatureLockRenewResponse(
    FeatureLockRenewStatus Status,
    FeatureLockLease? Lease = null,
    FeatureLockHeldError? Error = null,
    string? DenialReason = null)
{
    public bool IsSuccess => Status == FeatureLockRenewStatus.Renewed;
}

public enum FeatureLockReleaseStatus
{
    Released,

    NotFound,

    HeldByOther,

    Denied
}

public sealed record FeatureLockReleaseResponse(
    FeatureLockReleaseStatus Status,
    FeatureRef Feature,
    FeatureLockLease? ReleasedLease = null,
    FeatureLockHeldError? Error = null,
    string? DenialReason = null)
{
    public bool IsSuccess => Status == FeatureLockReleaseStatus.Released;
}

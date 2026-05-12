// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.FeatureLocks;

public readonly record struct FeatureRef(
    string ServiceName,
    int LayerId,
    string FeatureId);

public sealed record LockHolder(
    string HolderId,
    string? DisplayName = null,
    string? SessionId = null,
    string? TenantId = null);

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

public sealed record FeatureEditConflictResponse(
    string Code,
    string Reason,
    string Message,
    string Operation,
    FeatureRef Feature,
    FeatureLockHeldError? Lock,
    DateTimeOffset OccurredAt)
{
    public static FeatureEditConflictResponse FromLockHeld(
        string operation,
        FeatureLockHeldError lockError,
        DateTimeOffset occurredAt)
        => new(
            "edit-conflict",
            lockError.Code,
            "The edit cannot be applied because the target feature is locked.",
            operation,
            lockError.Feature,
            lockError,
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

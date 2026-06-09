// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.FeatureLocks;

/// <summary>
/// Identifies a single feature targeted by a collaborative editing lock.
/// </summary>
/// <param name="ServiceName">The name of the service that owns the feature.</param>
/// <param name="LayerId">The identifier of the layer that contains the feature.</param>
/// <param name="FeatureId">The provider-specific identifier of the feature.</param>
public readonly record struct FeatureRef(
    string ServiceName,
    int LayerId,
    string FeatureId);

/// <summary>
/// Describes the principal that holds or is requesting a feature lock.
/// </summary>
/// <param name="HolderId">The stable identifier of the lock holder.</param>
/// <param name="DisplayName">An optional human-readable name for the holder.</param>
/// <param name="SessionId">An optional editing session identifier for the holder.</param>
/// <param name="TenantId">An optional tenant identifier scoping the holder.</param>
public sealed record LockHolder(
    string HolderId,
    string? DisplayName = null,
    string? SessionId = null,
    string? TenantId = null);

/// <summary>
/// Represents an active lease on a feature lock, including its lifetime window.
/// </summary>
/// <param name="LockId">The unique identifier of the lock lease.</param>
/// <param name="Feature">The feature protected by the lease.</param>
/// <param name="Holder">The principal that holds the lease.</param>
/// <param name="AcquiredAt">The instant the lease was first acquired.</param>
/// <param name="ExpiresAt">The instant the lease expires unless renewed.</param>
/// <param name="RenewedAt">The instant the lease was most recently renewed, if any.</param>
public sealed record FeatureLockLease(
    string LockId,
    FeatureRef Feature,
    LockHolder Holder,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RenewedAt = null);

/// <summary>
/// Captures the authorization context used when evaluating feature lock operations.
/// </summary>
/// <param name="IsAuthorized">Whether the caller is authorized to access the feature.</param>
/// <param name="CanWrite">Whether the caller is permitted to perform write operations.</param>
public readonly record struct FeatureLockAccessContext(
    bool IsAuthorized,
    bool CanWrite)
{
    /// <summary>
    /// Gets a context that grants authorized write access.
    /// </summary>
    public static FeatureLockAccessContext AuthorizedWrite => new(IsAuthorized: true, CanWrite: true);

    /// <summary>
    /// Gets a context that denies all access.
    /// </summary>
    public static FeatureLockAccessContext Unauthorized => new(IsAuthorized: false, CanWrite: false);

    /// <summary>
    /// Gets a context that grants authorized read-only access.
    /// </summary>
    public static FeatureLockAccessContext ReadOnly => new(IsAuthorized: true, CanWrite: false);
}

/// <summary>
/// Describes an error returned when a feature is already locked by another editor.
/// </summary>
/// <param name="Code">A stable machine-readable error code.</param>
/// <param name="Message">A human-readable error message.</param>
/// <param name="Feature">The feature that is locked.</param>
/// <param name="Holder">The principal that currently holds the lock.</param>
/// <param name="ExpiresAt">The instant the conflicting lock expires.</param>
public sealed record FeatureLockHeldError(
    string Code,
    string Message,
    FeatureRef Feature,
    LockHolder Holder,
    DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Creates a <see cref="FeatureLockHeldError"/> from an existing lease.
    /// </summary>
    /// <param name="lease">The lease that caused the conflict.</param>
    /// <returns>A populated <see cref="FeatureLockHeldError"/>.</returns>
    public static FeatureLockHeldError FromLease(FeatureLockLease lease)
        => new(
            "feature-lock-held",
            "The feature is locked by another editor.",
            lease.Feature,
            lease.Holder,
            lease.ExpiresAt);
}

/// <summary>
/// Represents the response returned when a feature edit conflicts with an active lock.
/// </summary>
/// <param name="Code">A stable machine-readable conflict code.</param>
/// <param name="Reason">The underlying reason for the conflict.</param>
/// <param name="Message">A human-readable conflict message.</param>
/// <param name="Operation">The edit operation that was attempted.</param>
/// <param name="Feature">The feature that could not be edited.</param>
/// <param name="Lock">The lock error that triggered the conflict, if any.</param>
/// <param name="OccurredAt">The instant the conflict occurred.</param>
public sealed record FeatureEditConflictResponse(
    string Code,
    string Reason,
    string Message,
    string Operation,
    FeatureRef Feature,
    FeatureLockHeldError? Lock,
    DateTimeOffset OccurredAt)
{
    /// <summary>
    /// Creates a conflict response from a held-lock error.
    /// </summary>
    /// <param name="operation">The edit operation that was attempted.</param>
    /// <param name="lockError">The held-lock error that caused the conflict.</param>
    /// <param name="occurredAt">The instant the conflict occurred.</param>
    /// <returns>A populated <see cref="FeatureEditConflictResponse"/>.</returns>
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

/// <summary>
/// Describes the outcome of attempting to claim a feature lock.
/// </summary>
public enum FeatureLockClaimStatus
{
    /// <summary>A new lock was successfully claimed.</summary>
    Claimed,

    /// <summary>An existing lock held by the caller was renewed.</summary>
    Renewed,

    /// <summary>The feature is locked by another holder.</summary>
    HeldByOther,

    /// <summary>The claim was denied by authorization or policy.</summary>
    Denied
}

/// <summary>
/// Represents the response returned when claiming a feature lock.
/// </summary>
/// <param name="Status">The outcome of the claim attempt.</param>
/// <param name="Lease">The resulting lease when the claim succeeds.</param>
/// <param name="Error">The held-lock error when the feature is locked by another holder.</param>
/// <param name="DenialReason">The reason the claim was denied, if applicable.</param>
public sealed record FeatureLockClaimResponse(
    FeatureLockClaimStatus Status,
    FeatureLockLease? Lease = null,
    FeatureLockHeldError? Error = null,
    string? DenialReason = null)
{
    /// <summary>
    /// Gets a value indicating whether the claim resulted in an active lease.
    /// </summary>
    public bool IsSuccess => Status is FeatureLockClaimStatus.Claimed or FeatureLockClaimStatus.Renewed;
}

/// <summary>
/// Describes the outcome of attempting to renew a feature lock.
/// </summary>
public enum FeatureLockRenewStatus
{
    /// <summary>The lock was successfully renewed.</summary>
    Renewed,

    /// <summary>No matching lock was found to renew.</summary>
    NotFound,

    /// <summary>The lock is held by another holder.</summary>
    HeldByOther,

    /// <summary>The renewal was denied by authorization or policy.</summary>
    Denied
}

/// <summary>
/// Represents the response returned when renewing a feature lock.
/// </summary>
/// <param name="Status">The outcome of the renewal attempt.</param>
/// <param name="Lease">The renewed lease when the renewal succeeds.</param>
/// <param name="Error">The held-lock error when the feature is locked by another holder.</param>
/// <param name="DenialReason">The reason the renewal was denied, if applicable.</param>
public sealed record FeatureLockRenewResponse(
    FeatureLockRenewStatus Status,
    FeatureLockLease? Lease = null,
    FeatureLockHeldError? Error = null,
    string? DenialReason = null)
{
    /// <summary>
    /// Gets a value indicating whether the renewal succeeded.
    /// </summary>
    public bool IsSuccess => Status == FeatureLockRenewStatus.Renewed;
}

/// <summary>
/// Describes the outcome of attempting to release a feature lock.
/// </summary>
public enum FeatureLockReleaseStatus
{
    /// <summary>The lock was successfully released.</summary>
    Released,

    /// <summary>No matching lock was found to release.</summary>
    NotFound,

    /// <summary>The lock is held by another holder.</summary>
    HeldByOther,

    /// <summary>The release was denied by authorization or policy.</summary>
    Denied
}

/// <summary>
/// Represents the response returned when releasing a feature lock.
/// </summary>
/// <param name="Status">The outcome of the release attempt.</param>
/// <param name="Feature">The feature targeted by the release.</param>
/// <param name="ReleasedLease">The lease that was released, when applicable.</param>
/// <param name="Error">The held-lock error when the feature is locked by another holder.</param>
/// <param name="DenialReason">The reason the release was denied, if applicable.</param>
public sealed record FeatureLockReleaseResponse(
    FeatureLockReleaseStatus Status,
    FeatureRef Feature,
    FeatureLockLease? ReleasedLease = null,
    FeatureLockHeldError? Error = null,
    string? DenialReason = null)
{
    /// <summary>
    /// Gets a value indicating whether the release succeeded.
    /// </summary>
    public bool IsSuccess => Status == FeatureLockReleaseStatus.Released;
}

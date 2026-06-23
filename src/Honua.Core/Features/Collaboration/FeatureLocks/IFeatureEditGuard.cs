// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.FeatureLocks;

/// <summary>
/// Selects which concurrency-control checks a feature edit must satisfy before it
/// is allowed to mutate the backing store.
/// </summary>
public enum FeatureEditConcurrencyPolicy
{
    /// <summary>
    /// No concurrency control. The edit is always allowed (legacy behavior).
    /// </summary>
    None,

    /// <summary>
    /// The caller must hold an active lock on the feature. Edits without a held
    /// lease, or against a lease held by another editor, are rejected.
    /// </summary>
    RequireLock,

    /// <summary>
    /// The caller must supply an expected-version token that matches the version
    /// currently stored on the server. Stale tokens are rejected.
    /// </summary>
    RequireVersionToken,

    /// <summary>
    /// The edit is allowed when it satisfies <em>either</em> a held lock or a
    /// matching version token. This is the recommended default for collaborative
    /// editing: clients that hold a lock can edit without round-tripping a version
    /// token, and clients without a lock can still edit optimistically.
    /// </summary>
    LockOrVersionToken
}

/// <summary>
/// Describes a single feature mutation a caller intends to perform, along with the
/// concurrency evidence (lock holder and/or expected version token) supplied with it.
/// </summary>
/// <param name="Feature">The feature being edited.</param>
/// <param name="Operation">
/// The edit operation name (for example <c>update</c>, <c>delete</c>, or <c>replace</c>).
/// Surfaced verbatim on conflict responses so clients can present a useful prompt.
/// </param>
/// <param name="Holder">
/// The principal attempting the edit. Required for lock-based policies; may be
/// <see langword="null"/> for purely optimistic (version-token) edits.
/// </param>
/// <param name="ExpectedVersion">
/// The version token the client last observed for the feature, if any. Compared
/// against <see cref="CurrentVersion"/> for optimistic-concurrency policies.
/// </param>
/// <param name="CurrentVersion">
/// The version token currently stored on the server, if the backing provider
/// exposes one. <see langword="null"/> when the provider does not support
/// optimistic concurrency, in which case version checks are skipped.
/// </param>
public readonly record struct FeatureEditIntent(
    FeatureRef Feature,
    string Operation,
    LockHolder? Holder = null,
    string? ExpectedVersion = null,
    string? CurrentVersion = null);

/// <summary>
/// Describes the outcome of evaluating a <see cref="FeatureEditIntent"/>.
/// </summary>
public enum FeatureEditDecisionStatus
{
    /// <summary>The edit may proceed.</summary>
    Allowed,

    /// <summary>The edit is blocked by a lock held by another editor.</summary>
    LockConflict,

    /// <summary>The edit is blocked because the feature changed on the server.</summary>
    VersionConflict
}

/// <summary>
/// Represents the verdict returned by <see cref="IFeatureEditGuard"/> for a single
/// edit intent.
/// </summary>
/// <param name="Status">The decision outcome.</param>
/// <param name="Conflict">
/// The typed conflict response when the edit is rejected; <see langword="null"/>
/// when the edit is allowed.
/// </param>
public sealed record FeatureEditDecision(
    FeatureEditDecisionStatus Status,
    FeatureEditConflictResponse? Conflict = null)
{
    /// <summary>
    /// Gets a value indicating whether the edit is permitted to proceed.
    /// </summary>
    public bool IsAllowed => Status == FeatureEditDecisionStatus.Allowed;

    /// <summary>
    /// Gets a singleton decision that allows the edit.
    /// </summary>
    public static FeatureEditDecision Allow { get; } = new(FeatureEditDecisionStatus.Allowed);
}

/// <summary>
/// Evaluates whether a feature edit may proceed under a concurrency policy,
/// detecting lock conflicts and stale-version (optimistic concurrency) conflicts
/// and returning typed conflict metadata so clients can prompt for resolution.
/// </summary>
public interface IFeatureEditGuard
{
    /// <summary>
    /// Evaluates a single feature edit against the supplied concurrency policy.
    /// </summary>
    /// <param name="intent">The edit intent, including any concurrency evidence.</param>
    /// <param name="policy">The concurrency policy to enforce.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>
    /// A decision that either allows the edit or describes a typed conflict.
    /// </returns>
    ValueTask<FeatureEditDecision> EvaluateAsync(
        FeatureEditIntent intent,
        FeatureEditConcurrencyPolicy policy,
        CancellationToken cancellationToken = default);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// The kind of long-running branch-version maintenance operation a <see cref="VersionJob"/> wraps
/// (#1553). Reconcile diffs a branch version against DEFAULT and (optionally) auto-resolves conflicts;
/// post replays a reconciled version's net changes onto DEFAULT. Both run under the durable version
/// lock so a job and a conflicting request for the same version are serialized.
/// </summary>
public enum VersionJobKind
{
    /// <summary>Reconcile a branch version against DEFAULT.</summary>
    Reconcile = 0,

    /// <summary>Post a reconciled branch version's changes onto DEFAULT.</summary>
    Post = 1,
}

/// <summary>
/// Lifecycle status of an asynchronous branch-version reconcile/post job (#1553). A job is created in
/// <see cref="Pending"/>, transitions to <see cref="Running"/> once the worker acquires the version
/// lock and begins, and finishes in a terminal state. <see cref="LockContended"/> is a terminal state
/// used when the durable version lock could not be acquired because another reconcile/post for the same
/// version is already in flight.
/// </summary>
public enum VersionJobStatus
{
    /// <summary>The job has been created and durably recorded but has not started executing.</summary>
    Pending = 0,

    /// <summary>The job holds the version lock and is executing the reconcile/post.</summary>
    Running = 1,

    /// <summary>The job completed successfully.</summary>
    Succeeded = 2,

    /// <summary>The job failed with an error.</summary>
    Failed = 3,

    /// <summary>The version lock was held by another in-flight reconcile/post; the job did not run.</summary>
    LockContended = 4,
}

/// <summary>
/// Durable record of an asynchronous branch-version reconcile/post job (#1553). Carries enough state to
/// make the job pollable (status + outcome counts) and to resume/idempotently re-run after a restart:
/// the owning service and version, the requested reconcile policy, and the captured reconcile/post
/// result. The record never carries SQL, connection details, or provider internals.
/// </summary>
/// <param name="JobId">Stable job identifier.</param>
/// <param name="Service">Owning service identity used to scope the version lock.</param>
/// <param name="VersionId">Branch version the job operates on.</param>
/// <param name="Kind">Whether the job reconciles or posts.</param>
/// <param name="Status">Current lifecycle status.</param>
/// <param name="Policy">Reconcile auto-resolution policy (ignored for post jobs).</param>
/// <param name="CreatedAt">When the job was created.</param>
/// <param name="StartedAt">When the job acquired the lock and began, or null while pending.</param>
/// <param name="CompletedAt">When the job reached a terminal state, or null while not terminal.</param>
/// <param name="ConflictCount">Unresolved conflicts after a reconcile (0 for a clean reconcile or a post).</param>
/// <param name="AutoResolvedCount">Conflicts the policy auto-resolved during a reconcile.</param>
/// <param name="CanPost">True when a reconcile left the version clear to post.</param>
/// <param name="AppliedChanges">Net feature changes a post replayed onto DEFAULT.</param>
/// <param name="ServerGeneration">DEFAULT generation produced by a post (or reconciled-to generation).</param>
/// <param name="BlockedByConflicts">True when a post was refused because unresolved conflicts remain.</param>
/// <param name="ErrorMessage">Sanitized error message when <see cref="Status"/> is <see cref="VersionJobStatus.Failed"/>.</param>
public sealed record VersionJob(
    Guid JobId,
    string Service,
    Guid VersionId,
    VersionJobKind Kind,
    VersionJobStatus Status,
    VersionReconcilePolicy Policy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    int ConflictCount = 0,
    int AutoResolvedCount = 0,
    bool CanPost = false,
    int AppliedChanges = 0,
    long ServerGeneration = 0,
    bool BlockedByConflicts = false,
    string? ErrorMessage = null);

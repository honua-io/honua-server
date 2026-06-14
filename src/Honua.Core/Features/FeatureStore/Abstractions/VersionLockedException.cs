// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Thrown when a branch-version reconcile, post, or conflict resolution cannot start because the
/// durable (service, version) <see cref="IVersionLock"/> is already held by another in-flight
/// reconcile/post for the same version (#1553). Protocol adapters map this to a 409 in-progress
/// response; the async job runner records it as a <c>LockContended</c> terminal job.
/// </summary>
public sealed class VersionLockedException : Exception
{
    /// <summary>The version whose maintenance lock was contended.</summary>
    public Guid VersionId { get; }

    /// <summary>Initializes the exception for a contended version.</summary>
    /// <param name="versionId">The contended version.</param>
    public VersionLockedException(Guid versionId)
        : base($"A reconcile or post is already in progress for version {versionId}.")
        => VersionId = versionId;
}

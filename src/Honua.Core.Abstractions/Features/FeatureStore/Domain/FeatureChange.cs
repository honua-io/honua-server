// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents a single change to a feature recorded by the change-tracking trigger
/// </summary>
public readonly record struct FeatureChange
{
    /// <summary>
    /// Auto-incrementing change identifier
    /// </summary>
    public required long ChangeId { get; init; }

    /// <summary>
    /// Monotonic generation number from the sync_generation sequence
    /// </summary>
    public required long Generation { get; init; }

    /// <summary>
    /// Layer containing the changed feature
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Object ID of the changed feature
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Protocol-facing object ID captured from the layer's configured primary-id field when the
    /// change was written. This remains available after the feature row is deleted and can differ
    /// from <see cref="ObjectId"/>, which is the internal storage identity.
    /// </summary>
    public long? PublicObjectId { get; init; }

    /// <summary>Replica that uploaded this change, or null for ordinary server edits.</summary>
    public string? OriginReplicaId { get; init; }

    /// <summary>
    /// Change whose recorded pre-change image holds the row as it was before the first change in the
    /// collapsed window, or null when that state is unknown: the window starts with an insert, or the
    /// change predates pre-change image capture (branch-version overlays do not record one). Replica
    /// delivery evaluates the replica scope and the caller's row visibility against that image to tell
    /// a row the client already holds from one it has never received (#4879).
    /// </summary>
    public long? PreImageChangeId { get; init; }

    /// <summary>
    /// Type of change that occurred
    /// </summary>
    public required FeatureChangeOperation Operation { get; init; }

    /// <summary>
    /// Timestamp when the change was recorded
    /// </summary>
    public required DateTimeOffset ChangedAt { get; init; }
}

/// <summary>
/// Types of feature change operations tracked by the replication change log
/// </summary>
public enum FeatureChangeOperation : short
{
    /// <summary>
    /// A new feature was inserted
    /// </summary>
    Insert = 1,

    /// <summary>
    /// An existing feature was updated
    /// </summary>
    Update = 2,

    /// <summary>
    /// A feature was deleted
    /// </summary>
    Delete = 3
}

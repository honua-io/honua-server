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

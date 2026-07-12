// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Lifecycle state of an immutable routing-topology generation.
/// </summary>
public enum NetworkTopologyGenerationState
{
    /// <summary>A non-active generation allocated for an edit batch.</summary>
    Draft,

    /// <summary>A non-active generation containing edits that require a rebuild.</summary>
    Dirty,

    /// <summary>A worker is rebuilding the generation's isolated topology.</summary>
    Building,

    /// <summary>The rebuild completed and the generation is eligible for promotion.</summary>
    Ready,

    /// <summary>The generation is the immutable solve target for its dataset.</summary>
    Active,

    /// <summary>The generation failed with a sanitized, stable failure code.</summary>
    Failed,

    /// <summary>The generation is no longer promotable and is eligible for retention cleanup.</summary>
    Retired,
}

/// <summary>
/// Provider-neutral metadata for one immutable routing-topology generation.
/// </summary>
/// <param name="DatasetId">Stable network-dataset identifier.</param>
/// <param name="Generation">Monotonically increasing generation number within the dataset.</param>
/// <param name="SourceRevision">Monotonic content revision from which the topology is built.</param>
/// <param name="State">Current lifecycle state.</param>
/// <param name="RowVersion">Compare-and-swap version incremented by every state mutation.</param>
/// <param name="Srid">Spatial reference of the topology geometry.</param>
/// <param name="CreatedAt">Generation creation timestamp.</param>
/// <param name="UpdatedAt">Last lifecycle mutation timestamp.</param>
/// <param name="ActivatedAt">Timestamp at which this generation became active, if applicable.</param>
/// <param name="FailureCode">Sanitized stable failure code; raw exception details are never stored here.</param>
public sealed record NetworkTopologyGeneration(
    string DatasetId,
    long Generation,
    long SourceRevision,
    NetworkTopologyGenerationState State,
    long RowVersion,
    int Srid,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ActivatedAt,
    string? FailureCode);

/// <summary>
/// Stable reason that a topology lifecycle compare-and-swap operation was rejected.
/// </summary>
public enum NetworkTopologyTransitionFailure
{
    /// <summary>The transition succeeded.</summary>
    None,

    /// <summary>The caller's expected row version no longer matches persisted state.</summary>
    StaleRowVersion,

    /// <summary>The requested lifecycle transition violates the state machine.</summary>
    InvalidTransition,

    /// <summary>The failure code is not a sanitized stable identifier.</summary>
    InvalidFailureCode,
}

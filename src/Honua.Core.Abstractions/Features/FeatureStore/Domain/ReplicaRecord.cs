// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Persistent replica state for offline sync workflows
/// </summary>
public readonly record struct ReplicaRecord
{
    /// <summary>
    /// Unique replica identifier (GUID hex)
    /// </summary>
    public required string ReplicaId { get; init; }

    /// <summary>
    /// Human-readable replica name
    /// </summary>
    public required string ReplicaName { get; init; }

    /// <summary>
    /// Service the replica belongs to
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Sync model: none, perLayer, or perReplica
    /// </summary>
    public required string SyncModel { get; init; }

    /// <summary>
    /// Array of layer IDs included in the replica
    /// </summary>
    public required int[] LayerIds { get; init; }

    /// <summary>
    /// Timestamp when the replica was created
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp of the last successful sync
    /// </summary>
    public required DateTimeOffset LastSyncTime { get; init; }

    /// <summary>
    /// Generation number at last successful sync
    /// </summary>
    public required long LastSyncGeneration { get; init; }

    /// <summary>
    /// Server generation produced by the most recent upload sync. Used as the download "since"
    /// cursor so a replica does not receive its own just-applied edits back on a subsequent
    /// download delta. Defaults to 0 for replicas that have never uploaded (#1272).
    /// </summary>
    public long UploadBaseGeneration { get; init; }
}

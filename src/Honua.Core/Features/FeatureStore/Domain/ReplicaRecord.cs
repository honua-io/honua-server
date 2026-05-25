// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Persistent replica state for offline sync workflows
/// </summary>
public readonly record struct ReplicaRecord
{
    /// <summary>
    /// Initializes a replica record. Required members must still be set via object initializer;
    /// the explicit constructor exists so the defaulted metadata fields run their initializers.
    /// </summary>
    public ReplicaRecord()
    {
    }

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
    /// Principal that registered the replica (operator-visible owner). Optional.
    /// </summary>
    public string? Owner { get; init; }

    /// <summary>
    /// Device or client identifier that created the replica. Optional.
    /// </summary>
    public string? DeviceClient { get; init; }

    /// <summary>
    /// Sync direction: upload, download, or bidirectional. Defaults to bidirectional.
    /// </summary>
    public string SyncDirection { get; init; } = "bidirectional";

    /// <summary>
    /// Replica lifecycle status: active, stale, expired, or unregistered. Defaults to active.
    /// </summary>
    public string Status { get; init; } = "active";

    /// <summary>
    /// Optional raw GeoJSON spatial filter for the replica. CRS is validated on the
    /// createReplica path, not on read.
    /// </summary>
    public string? ReplicaGeometryJson { get; init; }

    /// <summary>
    /// Optional named branch-version reference for #371 interop. Unused in the first slice;
    /// reconcile/post remains #371 scope.
    /// </summary>
    public string? BranchVersionId { get; init; }
}

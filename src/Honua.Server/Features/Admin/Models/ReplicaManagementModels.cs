// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Operator-facing summary of a named replica registered against a feature service.
/// Surfaced by the admin replica-management list endpoint so Console and operators
/// can review active disconnected replicas (#1167, slice 1).
/// </summary>
public sealed class ReplicaManagementSummary
{
    /// <summary>
    /// Unique replica identifier (GUID hex).
    /// </summary>
    public required string ReplicaId { get; init; }

    /// <summary>
    /// Human-readable replica name supplied at creation time.
    /// </summary>
    public required string ReplicaName { get; init; }

    /// <summary>
    /// Identifier of the feature service the replica belongs to.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Sync model: none, perLayer, or perReplica.
    /// </summary>
    public required string SyncModel { get; init; }

    /// <summary>
    /// Service-local layer identifiers included in the replica.
    /// </summary>
    public required int[] LayerIds { get; init; }

    /// <summary>
    /// Timestamp (UTC) when the replica was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp (UTC) of the last successful synchronization.
    /// </summary>
    public required DateTimeOffset LastSyncTime { get; init; }

    /// <summary>
    /// Derived operator-facing status: <c>active</c> when the replica has synced within the
    /// staleness window, otherwise <c>expired</c>.
    /// </summary>
    public required string Status { get; init; }
}

/// <summary>
/// Operator-facing detail view of a single named replica, including the server
/// generation cursor captured at the last successful sync (#1167, slice 1).
/// </summary>
public sealed class ReplicaManagementDetail
{
    /// <summary>
    /// Unique replica identifier (GUID hex).
    /// </summary>
    public required string ReplicaId { get; init; }

    /// <summary>
    /// Human-readable replica name supplied at creation time.
    /// </summary>
    public required string ReplicaName { get; init; }

    /// <summary>
    /// Identifier of the feature service the replica belongs to.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Sync model: none, perLayer, or perReplica.
    /// </summary>
    public required string SyncModel { get; init; }

    /// <summary>
    /// Service-local layer identifiers included in the replica.
    /// </summary>
    public required int[] LayerIds { get; init; }

    /// <summary>
    /// Timestamp (UTC) when the replica was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp (UTC) of the last successful synchronization.
    /// </summary>
    public required DateTimeOffset LastSyncTime { get; init; }

    /// <summary>
    /// Server generation cursor recorded at the last successful synchronization.
    /// </summary>
    public required long LastSyncGeneration { get; init; }

    /// <summary>
    /// Derived operator-facing status: <c>active</c> when the replica has synced within the
    /// staleness window, otherwise <c>expired</c>.
    /// </summary>
    public required string Status { get; init; }
}

/// <summary>
/// Envelope returned by the replica-management list endpoint.
/// </summary>
public sealed class ReplicaManagementListResponse
{
    /// <summary>
    /// Identifier of the feature service whose replicas are listed.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Replica summaries registered against the service. May be empty.
    /// </summary>
    public required ReplicaManagementSummary[] Replicas { get; init; }
}

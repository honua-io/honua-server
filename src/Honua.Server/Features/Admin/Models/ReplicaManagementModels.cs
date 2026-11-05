// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class ReplicaManagementSummary
{
    public required string ReplicaId { get; init; }

    public required string ReplicaName { get; init; }

    public required string ServiceId { get; init; }

    public required string SyncModel { get; init; }

    public required int[] LayerIds { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset LastSyncTime { get; init; }

    public required string Status { get; init; }
}

public sealed class ReplicaManagementDetail
{
    public required string ReplicaId { get; init; }

    public required string ReplicaName { get; init; }

    public required string ServiceId { get; init; }

    public required string SyncModel { get; init; }

    public required int[] LayerIds { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset LastSyncTime { get; init; }

    public required long LastSyncGeneration { get; init; }

    public required string Status { get; init; }
}

public sealed class ReplicaManagementListResponse
{
    public required string ServiceId { get; init; }

    public required ReplicaManagementSummary[] Replicas { get; init; }
}

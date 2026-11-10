// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct ReplicaRecord
{
    public required string ReplicaId { get; init; }

    public required string ReplicaName { get; init; }

    public required string ServiceId { get; init; }

    public string? OwnerId { get; init; }

    public required string SyncModel { get; init; }

    public required int[] LayerIds { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset LastSyncTime { get; init; }

    public required long LastSyncGeneration { get; init; }

    // This is an upload checkpoint, not a download cursor, so unrelated server edits are still downloaded.
    public long UploadBaseGeneration { get; init; }

    public string? ScopeDefinition { get; init; }
}

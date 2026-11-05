// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

public sealed record SceneDatasetRecord
{
    public Guid DatasetId { get; init; }

    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string AssetRoot { get; init; }

    // A null prefix means the dataset stays on the node that published it and is not hydrated elsewhere.
    public string? AssetStoragePrefix { get; init; }

    public string TilesetFileName { get; init; } = "tileset.json";

    public SceneDatasetType DatasetType { get; init; } = SceneDatasetType.HostedTiles;

    public SceneExtent? Extent { get; init; }

    public string? Crs { get; init; }

    public SceneCachePolicy CachePolicy { get; init; } = SceneCachePolicy.Default;

    public string? EditionGate { get; init; }

    public bool RequiresAuth { get; init; }

    public bool IsPublic { get; init; } = true;

    public IReadOnlyList<string>? AllowedRoles { get; init; }

    public SceneDatasetStatus Status { get; init; } = SceneDatasetStatus.Active;

    public string? ValidationMessage { get; init; }

    public int Revision { get; init; } = 1;

    public DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

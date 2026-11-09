// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Scene.Domain;

public sealed record SceneDataset
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string AssetRoot { get; init; }

    public string TilesetFileName { get; init; } = "tileset.json";

    public AccessPolicy? AccessPolicy { get; init; }

    public SceneCachePolicy? CachePolicy { get; init; }
}

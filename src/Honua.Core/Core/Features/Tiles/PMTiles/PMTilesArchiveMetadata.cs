// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles.PMTiles;

public sealed record PMTilesArchiveMetadata
{
    public string Name { get; init; } = "Honua";

    public required double MinLon { get; init; }

    public required double MinLat { get; init; }

    public required double MaxLon { get; init; }

    public required double MaxLat { get; init; }

    public required int MinZoom { get; init; }

    public required int MaxZoom { get; init; }

    public double? CenterLon { get; init; }

    public double? CenterLat { get; init; }

    public int? CenterZoom { get; init; }

    public string? Attribution { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<PMTilesVectorLayerMetadata> VectorLayers { get; init; } = [];
}

public sealed record PMTilesVectorLayerMetadata
{
    public required string Id { get; init; }

    public string? Description { get; init; }

    public int? MinZoom { get; init; }

    public int? MaxZoom { get; init; }

    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}

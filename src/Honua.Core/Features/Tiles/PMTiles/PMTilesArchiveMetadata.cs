// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles.PMTiles;

/// <summary>
/// Metadata for a PMTiles archive, describing bounds, zoom, and attribution.
/// </summary>
public sealed record PMTilesArchiveMetadata
{
    /// <summary>Human-readable archive name.</summary>
    public string Name { get; init; } = "Honua";

    /// <summary>Minimum longitude of the tile data bounds.</summary>
    public required double MinLon { get; init; }

    /// <summary>Minimum latitude of the tile data bounds.</summary>
    public required double MinLat { get; init; }

    /// <summary>Maximum longitude of the tile data bounds.</summary>
    public required double MaxLon { get; init; }

    /// <summary>Maximum latitude of the tile data bounds.</summary>
    public required double MaxLat { get; init; }

    /// <summary>Minimum zoom level present in the archive.</summary>
    public required int MinZoom { get; init; }

    /// <summary>Maximum zoom level present in the archive.</summary>
    public required int MaxZoom { get; init; }

    /// <summary>Center longitude for default map view.</summary>
    public double? CenterLon { get; init; }

    /// <summary>Center latitude for default map view.</summary>
    public double? CenterLat { get; init; }

    /// <summary>Center zoom for default map view.</summary>
    public int? CenterZoom { get; init; }

    /// <summary>Attribution string for the tile data.</summary>
    public string? Attribution { get; init; }

    /// <summary>Human-readable description of the tile data.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Vector layer metadata required by PMTiles v3 for Mapbox Vector Tile archives.
    /// </summary>
    public IReadOnlyList<PMTilesVectorLayerMetadata> VectorLayers { get; init; } = [];
}

/// <summary>Metadata for one vector layer embedded in a PMTiles archive.</summary>
public sealed record PMTilesVectorLayerMetadata
{
    /// <summary>Stable source-layer identifier used by vector-tile clients.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable layer description.</summary>
    public string? Description { get; init; }

    /// <summary>Minimum zoom at which the layer is present.</summary>
    public int? MinZoom { get; init; }

    /// <summary>Maximum zoom at which the layer is present.</summary>
    public int? MaxZoom { get; init; }

    /// <summary>Attribute field names and their TileJSON type descriptions.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}

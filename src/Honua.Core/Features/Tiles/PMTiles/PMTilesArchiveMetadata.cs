// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles.PMTiles;

/// <summary>
/// Metadata for a PMTiles archive, describing bounds, zoom, and attribution.
/// </summary>
public sealed record PMTilesArchiveMetadata
{
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
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.SpatialAnalytics.Domain;

/// <summary>
/// Binning algorithms supported by the density endpoint.
/// </summary>
public enum DensityBinningMode
{
    /// <summary>
    /// Hexagonal cells generated with PostGIS <c>ST_HexagonGrid</c>.
    /// </summary>
    HexGrid,

    /// <summary>
    /// Square cells generated with PostGIS <c>ST_SquareGrid</c>.
    /// </summary>
    SquareGrid,
}

/// <summary>
/// Defines a density (heatmap) operation that bins input features into a hex
/// or square grid in Web Mercator (3857) and returns one row per occupied cell
/// with the cell boundary geometry plus a count or weighted sum.
/// </summary>
public readonly record struct DensityQuery
{
    /// <summary>
    /// Binning algorithm. Hex grids minimize neighbor-distance variance and are
    /// the default for visualization use cases.
    /// </summary>
    public required DensityBinningMode Mode { get; init; }

    /// <summary>
    /// Cell size in meters. Used as the hexagon edge length for
    /// <see cref="DensityBinningMode.HexGrid"/> or the square edge length for
    /// <see cref="DensityBinningMode.SquareGrid"/>.
    /// </summary>
    public required double CellSizeMeters { get; init; }

    /// <summary>
    /// Optional weight attribute. When set, each cell aggregates <c>SUM(weight)</c>
    /// rather than the row count.
    /// </summary>
    public string? WeightField { get; init; }

    /// <summary>
    /// Maximum number of output cells. The query applies <c>LIMIT n+1</c> after
    /// the GROUP BY so high-density inputs are detected and rejected before
    /// they materialize.
    /// </summary>
    public int MaxCells { get; init; }

    /// <summary>
    /// Maximum number of input features the operation will process. Enforced
    /// against the bbox-filtered subset, not the full layer.
    /// </summary>
    public int MaxInputFeatures { get; init; }

    /// <summary>
    /// Minimum allowed cell size in meters. Smaller cells produce extremely
    /// large grids and are rejected at the handler.
    /// </summary>
    public const double MinCellSizeMeters = 1d;

    /// <summary>
    /// Maximum allowed cell size in meters. Larger values are rejected to keep
    /// the grid generator from running unboundedly across world extents.
    /// </summary>
    public const double MaxCellSizeMeters = 1_000_000d;
}

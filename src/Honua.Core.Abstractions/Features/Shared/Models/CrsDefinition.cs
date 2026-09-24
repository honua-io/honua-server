// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Axis ordering for coordinate sequences.
/// </summary>
public enum AxisOrder
{
    /// <summary>
    /// Easting (X), then northing (Y).
    /// </summary>
    EastNorth,

    /// <summary>
    /// Northing (Y), then easting (X).
    /// </summary>
    NorthEast
}

/// <summary>
/// Canonical CRS definition with axis order and geographic classification.
/// </summary>
/// <param name="Uri">Canonical CRS URI.</param>
/// <param name="Srid">EPSG SRID.</param>
/// <param name="AxisOrder">Axis order for coordinates.</param>
/// <param name="IsGeographic">True when CRS is geographic (lat/lon).</param>
public readonly record struct CrsDefinition(string Uri, int Srid, AxisOrder AxisOrder, bool IsGeographic)
{
    /// <summary>
    /// OGC Well-Known Text representation of the CRS, when available from the spatial reference registry.
    /// </summary>
    public string? Wkt { get; init; }

    /// <summary>
    /// Unit conversion factor from the registry. For a projected CRS this is metres per
    /// native unit. For a geographic CRS this is radians per native angular unit.
    /// Null when the definition was built without a registry unit.
    /// </summary>
    public double? LinearUnitFactor { get; init; }
}

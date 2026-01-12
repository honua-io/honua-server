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
public readonly record struct CrsDefinition(string Uri, int Srid, AxisOrder AxisOrder, bool IsGeographic);

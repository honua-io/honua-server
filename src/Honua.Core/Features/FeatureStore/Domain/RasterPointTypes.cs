// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents a projected point coordinate returned directly from the database.
/// </summary>
public readonly record struct ProjectedPoint(double X, double Y);

/// <summary>
/// Describes a point-thinning grid in projected/output coordinates.
/// </summary>
public readonly record struct RasterPointGrid
{
    /// <summary>
    /// Minimum X of the render extent used as the horizontal grid origin.
    /// </summary>
    public required double OriginX { get; init; }

    /// <summary>
    /// Maximum Y of the render extent used as the vertical grid origin.
    /// </summary>
    public required double OriginY { get; init; }

    /// <summary>
    /// Cell width in projected/output units.
    /// </summary>
    public required double CellWidth { get; init; }

    /// <summary>
    /// Cell height in projected/output units.
    /// </summary>
    public required double CellHeight { get; init; }
}

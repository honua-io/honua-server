// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Geometry types supported by layers in the GeoServices specification
/// </summary>
public enum GeometryType
{
    /// <summary>
    /// Point geometry (single coordinate)
    /// </summary>
    Point,

    /// <summary>
    /// Multi-point geometry (collection of points)
    /// </summary>
    MultiPoint,

    /// <summary>
    /// Line string geometry (connected coordinates forming a line)
    /// </summary>
    LineString,

    /// <summary>
    /// Multi-line string geometry (collection of line strings)
    /// </summary>
    MultiLineString,

    /// <summary>
    /// Polygon geometry (closed area with possible holes)
    /// </summary>
    Polygon,

    /// <summary>
    /// Multi-polygon geometry (collection of polygons)
    /// </summary>
    MultiPolygon,

    /// <summary>
    /// Mixed geometry types in the same layer
    /// </summary>
    GeometryCollection,

    /// <summary>
    /// No geometry (attribute-only table)
    /// </summary>
    None
}

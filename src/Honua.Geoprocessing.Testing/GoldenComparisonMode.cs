// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.Testing;

/// <summary>
/// Selects how a <see cref="GpProcessTestRunner"/> compares a process's published
/// artifact against its golden file (GP Devkit P6, issue #2127).
/// </summary>
public enum GoldenComparisonMode
{
    /// <summary>
    /// Pick the comparator from the artifact's media type: a vector GeoJSON artifact
    /// (<c>application/geo+json</c>) is compared with the NTS coordinate-tolerance
    /// <see cref="Geometry"/> diff; anything else is compared with the numeric/structural
    /// <see cref="ScalarStructural"/> diff. This is the default and fits the common case
    /// where a fixture does not need to override the comparator.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Compare vector outputs as geometry: parse both the actual and golden GeoJSON,
    /// align features, and diff coordinates with a tolerance via NetTopologySuite — NOT
    /// a byte-for-byte equality. Non-geometry properties are compared structurally.
    /// </summary>
    Geometry = 1,

    /// <summary>
    /// Compare raster/scalar/tabular outputs structurally: JSON is diffed key-by-key with
    /// a numeric tolerance on number leaves; any other payload is diffed as normalized
    /// text. Use this for CSV/convert outputs, scalar metrics, and raster statistics.
    /// </summary>
    ScalarStructural = 2,
}

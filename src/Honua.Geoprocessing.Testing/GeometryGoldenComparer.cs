// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Testing;

/// <summary>
/// Compares a vector GeoJSON artifact against a golden GeoJSON file with a coordinate
/// tolerance via NetTopologySuite — NOT a byte-for-byte equality (GP Devkit P6, issue
/// #2127).
///
/// <para>
/// The comparison is structural-with-tolerance: both sides are parsed into NTS
/// <see cref="FeatureCollection"/>s (a bare <c>Feature</c> or <c>Geometry</c> is wrapped
/// into a single-feature collection), features are aligned positionally, each geometry is
/// checked for the same structure (type, ring/part/point counts) and then coordinate-by-
/// coordinate within <see cref="GoldenTolerance.Coordinate"/>, and non-geometry attributes
/// are diffed structurally with the numeric tolerance. This tolerates the least-significant
/// digit drift that buffer segmentation and reprojection produce across platforms while
/// still catching a genuinely different geometry.
/// </para>
/// </summary>
public static class GeometryGoldenComparer
{
    /// <summary>
    /// Compares <paramref name="actualGeoJson"/> against <paramref name="goldenGeoJson"/>.
    /// </summary>
    /// <param name="actualGeoJson">The GeoJSON the process produced.</param>
    /// <param name="goldenGeoJson">The recorded golden GeoJSON.</param>
    /// <param name="tolerance">The coordinate/numeric tolerances to apply.</param>
    /// <returns>A matched result, or a mismatch carrying located differences.</returns>
    public static GoldenComparisonResult Compare(
        string actualGeoJson,
        string goldenGeoJson,
        GoldenTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(actualGeoJson);
        ArgumentNullException.ThrowIfNull(goldenGeoJson);

        FeatureCollection actual;
        FeatureCollection golden;
        try
        {
            actual = ReadFeatures(actualGeoJson);
        }
        catch (Exception ex)
        {
            // Intentionally broad: any parse failure (malformed JSON, unsupported GeoJSON
            // shape, NTS reader errors) should surface as a structured golden mismatch
            // rather than crash the comparison, and the message is captured below.
            return GoldenComparisonResult.Mismatch(
                "Geometry golden comparison failed: the actual artifact is not valid GeoJSON.",
                [$"actual: {ex.Message}"]);
        }

        try
        {
            golden = ReadFeatures(goldenGeoJson);
        }
        catch (Exception ex)
        {
            // Intentionally broad: see rationale above for the actual-artifact parse.
            return GoldenComparisonResult.Mismatch(
                "Geometry golden comparison failed: the golden file is not valid GeoJSON.",
                [$"golden: {ex.Message}"]);
        }

        var differences = new List<string>();

        if (actual.Count != golden.Count)
        {
            differences.Add($"feature count: actual={actual.Count}, golden={golden.Count}");
            return GoldenComparisonResult.Mismatch(
                $"Geometry golden mismatch: feature count differs (actual={actual.Count}, golden={golden.Count}).",
                differences);
        }

        for (var i = 0; i < golden.Count; i++)
        {
            CompareFeature(i, actual[i], golden[i], tolerance, differences);
        }

        if (differences.Count == 0)
        {
            return GoldenComparisonResult.Match;
        }

        return GoldenComparisonResult.Mismatch(
            $"Geometry golden mismatch: {differences.Count} difference(s) beyond tolerance "
                + $"(coordinate={tolerance.Coordinate:R}, numeric={tolerance.Numeric:R}).",
            differences);
    }

    private static void CompareFeature(
        int index,
        IFeature actual,
        IFeature golden,
        GoldenTolerance tolerance,
        List<string> differences)
    {
        CompareGeometry($"feature[{index}]", actual.Geometry, golden.Geometry, tolerance, differences);
        AttributeGoldenComparer.Compare(
            $"feature[{index}].properties",
            actual.Attributes,
            golden.Attributes,
            tolerance,
            differences);
    }

    private static void CompareGeometry(
        string path,
        Geometry? actual,
        Geometry? golden,
        GoldenTolerance tolerance,
        List<string> differences)
    {
        if (actual is null || golden is null)
        {
            if (!ReferenceEquals(actual, golden))
            {
                differences.Add($"{path}.geometry: actual={Describe(actual)}, golden={Describe(golden)}");
            }

            return;
        }

        if (!string.Equals(actual.GeometryType, golden.GeometryType, StringComparison.Ordinal))
        {
            differences.Add(
                $"{path}.geometry type: actual={actual.GeometryType}, golden={golden.GeometryType}");
            return;
        }

        if (actual.NumGeometries != golden.NumGeometries)
        {
            differences.Add(
                $"{path}.geometry part count: actual={actual.NumGeometries}, golden={golden.NumGeometries}");
            return;
        }

        var actualCoords = actual.Coordinates;
        var goldenCoords = golden.Coordinates;
        if (actualCoords.Length != goldenCoords.Length)
        {
            differences.Add(
                $"{path}.geometry vertex count: actual={actualCoords.Length}, golden={goldenCoords.Length}");
            return;
        }

        for (var c = 0; c < goldenCoords.Length; c++)
        {
            var a = actualCoords[c];
            var g = goldenCoords[c];
            var dx = Math.Abs(a.X - g.X);
            var dy = Math.Abs(a.Y - g.Y);
            if (dx > tolerance.Coordinate || dy > tolerance.Coordinate)
            {
                differences.Add(
                    $"{path}.geometry coord[{c}]: actual=({a.X:R} {a.Y:R}), golden=({g.X:R} {g.Y:R}), "
                        + $"|dx|={dx:R}, |dy|={dy:R} > tol={tolerance.Coordinate:R}");
            }
        }
    }

    private static string Describe(Geometry? geometry) => geometry is null ? "<null>" : geometry.GeometryType;

    private static FeatureCollection ReadFeatures(string geoJson)
    {
        var reader = new GeoJsonReader();
        var trimmed = geoJson.TrimStart();

        // A FeatureCollection parses directly; a bare Feature or Geometry is wrapped into a
        // single-feature collection so the rest of the comparison is uniform.
        if (trimmed.Contains("\"FeatureCollection\"", StringComparison.Ordinal))
        {
            return reader.Read<FeatureCollection>(geoJson);
        }

        if (trimmed.Contains("\"Feature\"", StringComparison.Ordinal))
        {
            var feature = reader.Read<IFeature>(geoJson);
            return new FeatureCollection { feature };
        }

        var geometry = reader.Read<Geometry>(geoJson);
        return new FeatureCollection { new Feature(geometry, new AttributesTable()) };
    }
}

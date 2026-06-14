// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NetTopologySuite.Geometries;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Outcome of an import geometry size check.
/// </summary>
public enum ImportGeometrySizeStatus
{
    /// <summary>The geometry is within all configured size limits.</summary>
    WithinLimits = 0,

    /// <summary>The geometry exceeded the maximum vertex count.</summary>
    TooManyVertices = 1,

    /// <summary>The geometry exceeded the maximum ring count.</summary>
    TooManyRings = 2,
}

/// <summary>
/// The result of an <see cref="ImportGeometrySizeGuard"/> check.
/// </summary>
/// <param name="Status">The size-check outcome.</param>
/// <param name="Message">A human-readable message when a limit was exceeded; otherwise <see langword="null"/>.</param>
public readonly record struct ImportGeometrySizeResult(ImportGeometrySizeStatus Status, string? Message)
{
    /// <summary>Gets a value indicating whether the geometry is within all limits.</summary>
    public bool IsWithinLimits => Status == ImportGeometrySizeStatus.WithinLimits;
}

/// <summary>
/// Bounded, allocation-light guard that rejects oversized geometries during import <em>before</em>
/// they are serialized to WKB or have their coordinate arrays materialized.
/// </summary>
/// <remarks>
/// A single island-scale multipolygon can carry millions of vertices. Materializing its
/// <see cref="NtsGeometry.Coordinates"/> array (a full copy) and then writing its WKB can allocate
/// hundreds of megabytes, OOM-killing a memory-constrained serverless host (issue #1626). This guard
/// inspects only <see cref="NtsGeometry.NumPoints"/> and ring counts — both O(1)/O(rings) and
/// allocation-free — so the import can reject the feature deterministically instead of crashing. It
/// runs regardless of the optional full-geometry validation pass so the memory ceiling always holds.
/// </remarks>
public static class ImportGeometrySizeGuard
{
    /// <summary>
    /// Checks a geometry against vertex- and ring-count limits without materializing coordinate
    /// arrays or WKB.
    /// </summary>
    /// <param name="geometry">The geometry to check. <see langword="null"/> is treated as within limits.</param>
    /// <param name="maxVertices">The maximum allowed vertex count, or 0 to disable the vertex check.</param>
    /// <param name="maxRings">The maximum allowed ring count, or 0 to disable the ring check.</param>
    /// <returns>The size-check result.</returns>
    public static ImportGeometrySizeResult Check(NtsGeometry? geometry, int maxVertices, int maxRings)
    {
        if (geometry is null)
        {
            return new ImportGeometrySizeResult(ImportGeometrySizeStatus.WithinLimits, null);
        }

        if (maxVertices > 0)
        {
            // NumPoints walks the coordinate sequences' counts without copying coordinates.
            var vertexCount = geometry.NumPoints;
            if (vertexCount > maxVertices)
            {
                return new ImportGeometrySizeResult(
                    ImportGeometrySizeStatus.TooManyVertices,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Geometry vertex count ({0:N0}) exceeds the import limit ({1:N0}). "
                        + "Explode multipart features (e.g. ogr2ogr -explodecollections) or simplify the geometry before importing.",
                        vertexCount,
                        maxVertices));
            }
        }

        if (maxRings > 0)
        {
            var ringCount = CountRings(geometry);
            if (ringCount > maxRings)
            {
                return new ImportGeometrySizeResult(
                    ImportGeometrySizeStatus.TooManyRings,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Geometry ring count ({0:N0}) exceeds the import limit ({1:N0}). "
                        + "Explode multipart features or simplify the geometry before importing.",
                        ringCount,
                        maxRings));
            }
        }

        return new ImportGeometrySizeResult(ImportGeometrySizeStatus.WithinLimits, null);
    }

    /// <summary>
    /// Counts the total number of rings across all polygon members of a geometry, without copying
    /// coordinates.
    /// </summary>
    /// <param name="geometry">The geometry to inspect.</param>
    /// <returns>The total ring count (exterior + interior rings).</returns>
    public static int CountRings(NtsGeometry geometry)
    {
        return geometry switch
        {
            Polygon polygon => 1 + polygon.NumInteriorRings,
            MultiPolygon multiPolygon => SumRings(multiPolygon),
            GeometryCollection collection => SumRings(collection),
            _ => 0,
        };
    }

    private static int SumRings(GeometryCollection collection)
    {
        var total = 0;
        for (var i = 0; i < collection.NumGeometries; i++)
        {
            total += CountRings(collection.GetGeometryN(i));
        }

        return total;
    }
}

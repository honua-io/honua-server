// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Infrastructure.Geometries;

/// <summary>
/// Normalizes polygon ring winding to the right-hand rule (exterior rings counter-clockwise,
/// interior rings — holes — clockwise) shared by RFC 7946 §3.1.6 (GeoJSON), GML 3.2 / ISO 19107
/// surface patches, and KML polygons.
/// </summary>
/// <remarks>
/// <para>
/// PostGIS-backed primary output already enforces this orientation via <c>ST_ForcePolygonCCW</c>,
/// and the GeoServices NetTopologySuite path enforces the Esri-JSON convention explicitly. Secondary
/// emitters that hand a stored geometry straight to a serializer (a <see cref="GeoJsonWriter"/> or an
/// in-memory KML writer) would otherwise pass through whatever winding the storage holds — stored
/// data is frequently clockwise-exterior (Esri <c>applyEdits</c>, shapefile imports), which violates
/// the right-hand rule. Routing those emitters through this helper keeps output consistent across
/// protocols (#2745).
/// </para>
/// <para>
/// The helper is cheap on the common path: non-polygonal geometry and already-correctly-wound
/// polygons are returned by reference without cloning; a new geometry is materialized only when a
/// ring actually needs reversing.
/// </para>
/// </remarks>
internal static class RingWindingNormalizer
{
    /// <summary>
    /// Serializes <paramref name="geometry"/> to GeoJSON with right-hand-rule (RFC 7946) winding.
    /// </summary>
    /// <param name="writer">A GeoJSON writer. Not thread-safe; callers must not share it concurrently.</param>
    /// <param name="geometry">The geometry to serialize.</param>
    /// <returns>The GeoJSON representation with normalized polygon ring winding.</returns>
    public static string WriteGeoJson(GeoJsonWriter writer, Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(geometry);
        return writer.Write(NormalizeToRightHandRule(geometry));
    }

    /// <summary>
    /// Returns a geometry whose polygonal rings follow the right-hand rule (exterior CCW, holes CW).
    /// Non-polygonal geometry and already-normalized polygons are returned unchanged (by reference);
    /// otherwise a reoriented copy is produced.
    /// </summary>
    /// <param name="geometry">The geometry to normalize.</param>
    /// <returns>A geometry with normalized ring winding.</returns>
    public static Geometry NormalizeToRightHandRule(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return Reorient(geometry) ?? geometry;
    }

    // Returns null when no reorientation is required (so callers keep the original reference);
    // otherwise returns a reoriented copy.
    private static Geometry? Reorient(Geometry geometry) => geometry switch
    {
        Polygon polygon => ReorientPolygon(polygon),
        MultiPolygon multiPolygon => ReorientMultiPolygon(multiPolygon),
        // Points, lines, and their multis carry no ring orientation, but a heterogeneous
        // GeometryCollection may still contain polygons that need reorienting.
        GeometryCollection collection => ReorientCollection(collection),
        _ => null,
    };

    private static Polygon? ReorientPolygon(Polygon polygon)
    {
        if (polygon.IsEmpty)
        {
            return null;
        }

        var shell = (LinearRing)polygon.ExteriorRing;
        var newShell = EnsureOrientation(shell, wantCcw: true);

        LinearRing[]? holes = null;
        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            var hole = (LinearRing)polygon.GetInteriorRingN(i);
            var newHole = EnsureOrientation(hole, wantCcw: false);
            if (!ReferenceEquals(newHole, hole))
            {
                holes ??= CopyInteriorRings(polygon);
                holes[i] = newHole;
            }
        }

        if (ReferenceEquals(newShell, shell) && holes is null)
        {
            return null;
        }

        holes ??= CopyInteriorRings(polygon);
        return polygon.Factory.CreatePolygon(newShell, holes);
    }

    private static MultiPolygon? ReorientMultiPolygon(MultiPolygon multiPolygon)
    {
        Polygon[]? polygons = null;
        for (var i = 0; i < multiPolygon.NumGeometries; i++)
        {
            var reoriented = ReorientPolygon((Polygon)multiPolygon.GetGeometryN(i));
            if (reoriented is not null)
            {
                polygons ??= CopyPolygons(multiPolygon);
                polygons[i] = reoriented;
            }
        }

        return polygons is null ? null : multiPolygon.Factory.CreateMultiPolygon(polygons);
    }

    private static GeometryCollection? ReorientCollection(GeometryCollection collection)
    {
        Geometry[]? geometries = null;
        for (var i = 0; i < collection.NumGeometries; i++)
        {
            var reoriented = Reorient(collection.GetGeometryN(i));
            if (reoriented is not null)
            {
                geometries ??= CopyGeometries(collection);
                geometries[i] = reoriented;
            }
        }

        return geometries is null ? null : collection.Factory.CreateGeometryCollection(geometries);
    }

    private static LinearRing EnsureOrientation(LinearRing ring, bool wantCcw)
    {
        // A valid ring needs at least four positions; degenerate rings have no meaningful winding.
        if (ring.IsEmpty || ring.NumPoints < 4)
        {
            return ring;
        }

        var isCcw = Orientation.IsCCW(ring.CoordinateSequence);
        return isCcw == wantCcw ? ring : (LinearRing)ring.Reverse();
    }

    private static LinearRing[] CopyInteriorRings(Polygon polygon)
    {
        var holes = new LinearRing[polygon.NumInteriorRings];
        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            holes[i] = (LinearRing)polygon.GetInteriorRingN(i);
        }

        return holes;
    }

    private static Polygon[] CopyPolygons(MultiPolygon multiPolygon)
    {
        var polygons = new Polygon[multiPolygon.NumGeometries];
        for (var i = 0; i < multiPolygon.NumGeometries; i++)
        {
            polygons[i] = (Polygon)multiPolygon.GetGeometryN(i);
        }

        return polygons;
    }

    private static Geometry[] CopyGeometries(GeometryCollection collection)
    {
        var geometries = new Geometry[collection.NumGeometries];
        for (var i = 0; i < collection.NumGeometries; i++)
        {
            geometries[i] = collection.GetGeometryN(i);
        }

        return geometries;
    }
}

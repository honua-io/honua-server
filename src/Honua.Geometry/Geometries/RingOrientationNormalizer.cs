// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;

namespace Honua.Core.Geometries;

/// <summary>
/// Shared reorientation core that rewrites polygonal ring winding to a caller-chosen orientation.
/// Both the secondary-emitter right-hand-rule normalizer (RFC 7946 / GML 3.2 / KML) and the SQL
/// Server geography filter normalizer delegate here so a single algorithm governs ring winding
/// across the codebase (#2745).
/// </summary>
/// <remarks>
/// <para>
/// Orientation is expressed by <c>wantExteriorCcw</c>: <see langword="true"/> selects the
/// right-hand rule (exterior ring counter-clockwise, interior rings — holes — clockwise), which is
/// also what SQL Server's <c>geography</c> type requires for its filter geometries. Interior rings
/// always take the opposite orientation of the exterior ring.
/// </para>
/// <para>
/// The core is cheap on the common path: non-polygonal geometry and already-correctly-wound
/// polygons are reported as "no change" (via a <see langword="null"/> return from
/// <see cref="Reorient"/>) so callers can keep the original reference; a new geometry is
/// materialized only when a ring actually needs reversing. Reoriented copies preserve the source
/// geometry's <see cref="Geometry.SRID"/>, which the geometry factory does not carry over on its
/// own.
/// </para>
/// </remarks>
public static class RingOrientationNormalizer
{
    /// <summary>
    /// Returns a geometry whose polygonal rings follow the requested orientation. Non-polygonal
    /// geometry and already-correctly-wound polygons are returned unchanged (by reference).
    /// </summary>
    /// <param name="geometry">The geometry to normalize.</param>
    /// <param name="wantExteriorCcw">
    /// <see langword="true"/> for a counter-clockwise exterior ring (right-hand rule / SQL Server
    /// geography); <see langword="false"/> for a clockwise exterior ring.
    /// </param>
    /// <returns>A geometry with normalized ring winding.</returns>
    public static Geometry Normalize(Geometry geometry, bool wantExteriorCcw)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return Reorient(geometry, wantExteriorCcw) ?? geometry;
    }

    /// <summary>
    /// Returns a reoriented copy of <paramref name="geometry"/>, or <see langword="null"/> when no
    /// reorientation is required so the caller can keep the original reference (and, for byte-based
    /// callers, the original serialized form).
    /// </summary>
    /// <param name="geometry">The geometry to inspect.</param>
    /// <param name="wantExteriorCcw">
    /// <see langword="true"/> for a counter-clockwise exterior ring; <see langword="false"/> for a
    /// clockwise exterior ring.
    /// </param>
    /// <returns>A reoriented copy, or <see langword="null"/> when the input already conforms.</returns>
    public static Geometry? Reorient(Geometry geometry, bool wantExteriorCcw)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return geometry switch
        {
            Polygon polygon => ReorientPolygon(polygon, wantExteriorCcw),
            MultiPolygon multiPolygon => ReorientMultiPolygon(multiPolygon, wantExteriorCcw),
            // Points, lines, and their multis carry no ring orientation, but a heterogeneous
            // GeometryCollection may still contain polygons that need reorienting.
            GeometryCollection collection => ReorientCollection(collection, wantExteriorCcw),
            _ => null,
        };
    }

    private static Polygon? ReorientPolygon(Polygon polygon, bool wantExteriorCcw)
    {
        if (polygon.IsEmpty)
        {
            return null;
        }

        var shell = (LinearRing)polygon.ExteriorRing;
        var newShell = EnsureOrientation(shell, wantCcw: wantExteriorCcw);

        LinearRing[]? holes = null;
        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            var hole = (LinearRing)polygon.GetInteriorRingN(i);
            var newHole = EnsureOrientation(hole, wantCcw: !wantExteriorCcw);
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
        var result = polygon.Factory.CreatePolygon(newShell, holes);
        result.SRID = polygon.SRID;
        return result;
    }

    private static MultiPolygon? ReorientMultiPolygon(MultiPolygon multiPolygon, bool wantExteriorCcw)
    {
        Polygon[]? polygons = null;
        for (var i = 0; i < multiPolygon.NumGeometries; i++)
        {
            var reoriented = ReorientPolygon((Polygon)multiPolygon.GetGeometryN(i), wantExteriorCcw);
            if (reoriented is not null)
            {
                polygons ??= CopyComponents<Polygon>(multiPolygon);
                polygons[i] = reoriented;
            }
        }

        if (polygons is null)
        {
            return null;
        }

        var result = multiPolygon.Factory.CreateMultiPolygon(polygons);
        result.SRID = multiPolygon.SRID;
        return result;
    }

    private static GeometryCollection? ReorientCollection(GeometryCollection collection, bool wantExteriorCcw)
    {
        Geometry[]? geometries = null;
        for (var i = 0; i < collection.NumGeometries; i++)
        {
            var reoriented = Reorient(collection.GetGeometryN(i), wantExteriorCcw);
            if (reoriented is not null)
            {
                geometries ??= CopyComponents<Geometry>(collection);
                geometries[i] = reoriented;
            }
        }

        if (geometries is null)
        {
            return null;
        }

        var result = collection.Factory.CreateGeometryCollection(geometries);
        result.SRID = collection.SRID;
        return result;
    }

    private static LinearRing EnsureOrientation(LinearRing ring, bool wantCcw)
    {
        // A valid ring needs at least four positions; degenerate rings have no meaningful winding.
        if (ring.IsEmpty || ring.NumPoints < 4)
        {
            return ring;
        }

        var isCcw = Orientation.IsCCW(ring.CoordinateSequence);
        if (isCcw == wantCcw)
        {
            return ring;
        }

        // LinearRing.Reverse() is [Obsolete("Call Geometry.Reverse()")] — it just forwards to
        // base.Reverse(), which still virtually dispatches to LinearRing's own ReverseInternal()
        // override. Calling through the Geometry-typed reference produces identical output while
        // binding to the non-obsolete overload.
        Geometry baseGeometry = ring;
        return (LinearRing)baseGeometry.Reverse();
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

    // Shallow copy of a homogeneous geometry container's members. MultiPolygon and
    // GeometryCollection both expose their parts via GetGeometryN, so one generic helper covers
    // both; interior rings use the distinct GetInteriorRingN accessor and keep CopyInteriorRings.
    private static T[] CopyComponents<T>(Geometry container)
        where T : Geometry
    {
        var items = new T[container.NumGeometries];
        for (var i = 0; i < container.NumGeometries; i++)
        {
            items[i] = (T)container.GetGeometryN(i);
        }

        return items;
    }
}

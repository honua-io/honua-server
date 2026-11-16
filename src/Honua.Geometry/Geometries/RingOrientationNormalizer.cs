// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;

namespace Honua.Core.Geometries;

public static class RingOrientationNormalizer
{
    public static Geometry Normalize(Geometry geometry, bool wantExteriorCcw)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return Reorient(geometry, wantExteriorCcw) ?? geometry;
    }

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

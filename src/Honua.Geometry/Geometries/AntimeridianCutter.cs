// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Geometries;

/// <summary>
/// Cuts a geographic polygon whose ring jumps the long way across the antimeridian
/// into a multipolygon of the short side. Edges of 180 degrees or less, including a
/// ring that only touches ±180, are returned unchanged.
/// </summary>
public static class AntimeridianCutter
{
    /// <summary>
    /// Returns <paramref name="geometry"/> when it is not a geographic polygon with a
    /// segment longer than 180 degrees. Otherwise returns the short-side coverage.
    /// </summary>
    public static Geometry Cut(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.IsEmpty
            || geometry.SRID <= 0
            || !GeographicSridClassifier.IsGeographicSrid(geometry.SRID)
            || !HasLongEdge(geometry))
        {
            return geometry;
        }

        var shifted = geometry.Copy();
        shifted.Apply(new ShiftNegativeLongitudeFilter());
        var envelope = shifted.EnvelopeInternal;
        var factory = shifted.Factory;
        var westBox = factory.ToGeometry(new Envelope(envelope.MinX - 1d, 180d, envelope.MinY - 1d, envelope.MaxY + 1d));
        var eastBox = factory.ToGeometry(new Envelope(180d, envelope.MaxX + 1d, envelope.MinY - 1d, envelope.MaxY + 1d));
        var west = shifted.Intersection(westBox);
        var east = shifted.Intersection(eastBox);
        if (!east.IsEmpty)
        {
            east = AffineTransformation.TranslationInstance(-360d, 0d).Transform(east);
        }

        var polygons = new List<Polygon>();
        CollectPolygons(west, polygons);
        CollectPolygons(east, polygons);
        if (polygons.Count == 0)
        {
            return geometry;
        }

        Geometry result = polygons.Count == 1
            ? polygons[0]
            : factory.CreateMultiPolygon(polygons.ToArray());
        result.SRID = geometry.SRID;
        return result;
    }

    private static bool HasLongEdge(Geometry geometry)
    {
        if (geometry is Polygon polygon)
        {
            if (RingHasLongEdge(polygon.ExteriorRing))
            {
                return true;
            }

            for (var hole = 0; hole < polygon.NumInteriorRings; hole++)
            {
                if (RingHasLongEdge(polygon.GetInteriorRingN(hole)))
                {
                    return true;
                }
            }

            return false;
        }

        if (geometry is not GeometryCollection)
        {
            return false;
        }

        for (var i = 0; i < geometry.NumGeometries; i++)
        {
            if (HasLongEdge(geometry.GetGeometryN(i)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RingHasLongEdge(LineString ring)
    {
        var count = ring.NumPoints;
        if (count < 2)
        {
            return false;
        }

        var previous = ring.GetCoordinateN(0).X;
        for (var i = 1; i < count; i++)
        {
            var longitude = ring.GetCoordinateN(i).X;
            if (Math.Abs(longitude - previous) > 180d)
            {
                return true;
            }

            previous = longitude;
        }

        return false;
    }

    private static void CollectPolygons(Geometry geometry, List<Polygon> polygons)
    {
        if (geometry.IsEmpty)
        {
            return;
        }

        if (geometry is Polygon polygon)
        {
            polygons.Add(polygon);
            return;
        }

        for (var i = 0; i < geometry.NumGeometries; i++)
        {
            CollectPolygons(geometry.GetGeometryN(i), polygons);
        }
    }

    private sealed class ShiftNegativeLongitudeFilter : ICoordinateSequenceFilter
    {
        public bool Done => false;

        public bool GeometryChanged { get; private set; }

        public void Filter(CoordinateSequence seq, int i)
        {
            var longitude = seq.GetX(i);
            if (longitude < 0d)
            {
                seq.SetX(i, longitude + 360d);
                GeometryChanged = true;
            }
        }
    }
}

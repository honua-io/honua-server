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

        // A mixed collection is not a polygon. Cut the polygon members and keep every
        // point and line; dropping them would store a multipolygon and report success.
        if (geometry is GeometryCollection collection && HasNonPolygon(collection))
        {
            return CutMixedCollection(collection);
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

    /// <summary>
    /// Cuts a geometry that will be stored in <paramref name="targetSrid"/>.
    /// An already-geographic geometry is cut in place. A Web Mercator geometry whose
    /// target is geographic is inverse-projected first, then cut, and the cut result
    /// is returned in the target SRID so a later <c>ST_Transform</c> does not rebuild
    /// the long edge. A ring that does not cross is returned unchanged so the database
    /// transform stays the source of the stored ordinates.
    /// </summary>
    public static Geometry CutStoredGeometry(Geometry geometry, int targetSrid)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (GeographicSridClassifier.IsGeographicSrid(geometry.SRID))
        {
            return Cut(geometry);
        }

        if (!GeographicSridClassifier.IsGeographicSrid(targetSrid)
            || !SpatialReferenceExtensions.IsWebMercatorSrid(geometry.SRID))
        {
            return geometry;
        }

        var geographic = geometry.Copy();
        geographic.Apply(new WebMercatorToGeographicFilter());
        geographic.SRID = targetSrid;
        var cut = Cut(geographic);
        return ReferenceEquals(cut, geographic) ? geometry : cut;
    }

    private static GeometryCollection CutMixedCollection(GeometryCollection collection)
    {
        var parts = new List<Geometry>(collection.NumGeometries);
        var changed = false;
        for (var i = 0; i < collection.NumGeometries; i++)
        {
            var child = collection.GetGeometryN(i);
            var cut = Cut(child);
            if (!ReferenceEquals(cut, child))
            {
                changed = true;
            }

            parts.Add(cut);
        }

        if (!changed)
        {
            return collection;
        }

        var result = collection.Factory.CreateGeometryCollection(parts.ToArray());
        result.SRID = collection.SRID;
        return result;
    }

    private static bool HasNonPolygon(Geometry geometry)
    {
        switch (geometry)
        {
            case Polygon:
            case MultiPolygon:
                return false;
            case GeometryCollection collection:
                for (var i = 0; i < collection.NumGeometries; i++)
                {
                    if (HasNonPolygon(collection.GetGeometryN(i)))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return true;
        }
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

    private sealed class WebMercatorToGeographicFilter : ICoordinateSequenceFilter
    {
        public bool Done => false;

        public bool GeometryChanged { get; private set; }

        public void Filter(CoordinateSequence seq, int i)
        {
            var (longitude, latitude) = WebMercatorMath.WebMercatorToLonLat(seq.GetX(i), seq.GetY(i));
            seq.SetX(i, longitude);
            seq.SetY(i, latitude);
            GeometryChanged = true;
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

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Geometries;

public static class AntimeridianCutter
{
    public static bool CrossesAntimeridian(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return !geometry.IsEmpty
            && (geometry.SRID <= 0 || GeographicSridClassifier.IsGeographicSrid(geometry.SRID))
            && HasLongEdge(geometry);
    }

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

        return geometry switch
        {
            Polygon polygon => CutPolygon(polygon, geometry.SRID),
            MultiPolygon multiPolygon => CutMultiPolygon(multiPolygon, geometry.SRID),
            LineString lineString => CutLineString(lineString, geometry.SRID),
            MultiLineString multiLineString => CutMultiLineString(multiLineString, geometry.SRID),
            GeometryCollection collection => CutMixedCollection(collection),
            _ => geometry
        };
    }

    private static Geometry CutPolygon(Polygon polygon, int srid)
    {
        var polygons = new List<Polygon>();
        if (!CutSinglePolygon(polygon, polygons))
        {
            return polygon;
        }

        Geometry result = polygons.Count == 1
            ? polygons[0]
            : polygon.Factory.CreateMultiPolygon(polygons.ToArray());
        result.SRID = srid;
        return result;
    }

    private static MultiPolygon CutMultiPolygon(MultiPolygon multiPolygon, int srid)
    {
        var polygons = new List<Polygon>(multiPolygon.NumGeometries * 2);
        var changed = false;

        for (var i = 0; i < multiPolygon.NumGeometries; i++)
        {
            var child = (Polygon)multiPolygon.GetGeometryN(i);
            if (!HasLongEdge(child))
            {
                polygons.Add(child);
                continue;
            }

            if (CutSinglePolygon(child, polygons))
            {
                changed = true;
            }
            else
            {
                polygons.Add(child);
            }
        }

        if (!changed || polygons.Count == 0)
        {
            return multiPolygon;
        }

        var result = multiPolygon.Factory.CreateMultiPolygon(polygons.ToArray());
        result.SRID = srid;
        return result;
    }

    private static bool CutSinglePolygon(Polygon polygon, List<Polygon> output)
    {
        if (!HasLongEdge(polygon))
        {
            return false;
        }

        var (hasZ, hasM) = DetectOrdinates(polygon);
        var shifted = (Polygon)polygon.Copy();
        shifted.Apply(new ShiftNegativeLongitudeFilter());
        var envelope = shifted.EnvelopeInternal;
        var factory = shifted.Factory;
        var westBox = factory.ToGeometry(new Envelope(envelope.MinX - 1d, 180d, envelope.MinY - 1d, envelope.MaxY + 1d));
        var eastBox = factory.ToGeometry(new Envelope(180d, envelope.MaxX + 1d, envelope.MinY - 1d, envelope.MaxY + 1d));
        var west = shifted.Intersection(westBox);
        var east = shifted.Intersection(eastBox);

        if (hasZ || hasM)
        {
            west = RestoreOrdinates(west, shifted, hasZ, hasM);
            east = RestoreOrdinates(east, shifted, hasZ, hasM);
        }

        if (!east.IsEmpty)
        {
            east.Apply(new UnshiftLongitudeFilter());
            east.GeometryChanged();
        }

        var startCount = output.Count;
        CollectPolygons(west, output);
        CollectPolygons(east, output);
        return output.Count > startCount;
    }

    private static Geometry CutLineString(LineString lineString, int srid)
    {
        var lines = new List<LineString>();
        if (!CutSingleLineString(lineString, lines))
        {
            return lineString;
        }

        Geometry result = lines.Count == 1
            ? lines[0]
            : lineString.Factory.CreateMultiLineString(lines.ToArray());
        result.SRID = srid;
        return result;
    }

    private static MultiLineString CutMultiLineString(MultiLineString multiLineString, int srid)
    {
        var lines = new List<LineString>(multiLineString.NumGeometries * 2);
        var changed = false;

        for (var i = 0; i < multiLineString.NumGeometries; i++)
        {
            var child = (LineString)multiLineString.GetGeometryN(i);
            if (!RingHasLongEdge(child))
            {
                lines.Add(child);
                continue;
            }

            if (CutSingleLineString(child, lines))
            {
                changed = true;
            }
            else
            {
                lines.Add(child);
            }
        }

        if (!changed || lines.Count == 0)
        {
            return multiLineString;
        }

        var result = multiLineString.Factory.CreateMultiLineString(lines.ToArray());
        result.SRID = srid;
        return result;
    }

    private static bool CutSingleLineString(LineString lineString, List<LineString> output)
    {
        if (!RingHasLongEdge(lineString))
        {
            return false;
        }

        var (hasZ, hasM) = DetectOrdinates(lineString);
        var shifted = (LineString)lineString.Copy();
        shifted.Apply(new ShiftNegativeLongitudeFilter());
        var envelope = shifted.EnvelopeInternal;
        var factory = shifted.Factory;
        var westBox = factory.ToGeometry(new Envelope(envelope.MinX - 1d, 180d, envelope.MinY - 1d, envelope.MaxY + 1d));
        var eastBox = factory.ToGeometry(new Envelope(180d, envelope.MaxX + 1d, envelope.MinY - 1d, envelope.MaxY + 1d));
        var west = shifted.Intersection(westBox);
        var east = shifted.Intersection(eastBox);

        if (hasZ || hasM)
        {
            west = RestoreOrdinates(west, shifted, hasZ, hasM);
            east = RestoreOrdinates(east, shifted, hasZ, hasM);
        }

        if (!east.IsEmpty)
        {
            east.Apply(new UnshiftLongitudeFilter());
            east.GeometryChanged();
        }

        var startCount = output.Count;
        CollectLineStrings(west, output);
        CollectLineStrings(east, output);
        return output.Count > startCount;
    }

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
            if (child.SRID <= 0 && collection.SRID > 0)
            {
                child = child.Copy();
                child.SRID = collection.SRID;
            }

            var cut = Cut(child);
            if (!ReferenceEquals(cut, child))
            {
                changed = true;
                parts.Add(cut);
            }
            else
            {
                parts.Add(collection.GetGeometryN(i));
            }
        }

        if (!changed)
        {
            return collection;
        }

        var result = collection.Factory.CreateGeometryCollection(parts.ToArray());
        result.SRID = collection.SRID;
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

        if (geometry is LineString lineString)
        {
            return RingHasLongEdge(lineString);
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

    private static void CollectLineStrings(Geometry geometry, List<LineString> lines)
    {
        if (geometry.IsEmpty)
        {
            return;
        }

        if (geometry is LineString line)
        {
            lines.Add(line);
            return;
        }

        for (var i = 0; i < geometry.NumGeometries; i++)
        {
            CollectLineStrings(geometry.GetGeometryN(i), lines);
        }
    }

    private static (bool HasZ, bool HasM) DetectOrdinates(Geometry geometry)
    {
        var hasZ = false;
        var hasM = false;
        foreach (var coord in geometry.Coordinates)
        {
            hasZ |= !double.IsNaN(coord.Z);
            hasM |= !double.IsNaN(coord.M);
            if (hasZ && hasM)
            {
                break;
            }
        }

        return (hasZ, hasM);
    }

    private static Geometry RestoreOrdinates(Geometry cutGeometry, Geometry shiftedSource, bool hasZ, bool hasM)
    {
        if (cutGeometry.IsEmpty)
        {
            return cutGeometry;
        }

        var sourceRings = ExtractLinearCoordinateArrays(shiftedSource);
        return cutGeometry switch
        {
            Polygon poly => RebuildPolygonWithOrdinates(poly, sourceRings, hasZ, hasM),
            LineString line => RebuildLineStringWithOrdinates(line, sourceRings, hasZ, hasM),
            MultiPolygon mpoly => cutGeometry.Factory.CreateMultiPolygon(
                Enumerable.Range(0, mpoly.NumGeometries)
                    .Select(i => RebuildPolygonWithOrdinates((Polygon)mpoly.GetGeometryN(i), sourceRings, hasZ, hasM))
                    .ToArray()),
            MultiLineString mline => cutGeometry.Factory.CreateMultiLineString(
                Enumerable.Range(0, mline.NumGeometries)
                    .Select(i => RebuildLineStringWithOrdinates((LineString)mline.GetGeometryN(i), sourceRings, hasZ, hasM))
                    .ToArray()),
            GeometryCollection coll => cutGeometry.Factory.CreateGeometryCollection(
                Enumerable.Range(0, coll.NumGeometries)
                    .Select(i => RestoreOrdinates(coll.GetGeometryN(i), shiftedSource, hasZ, hasM))
                    .ToArray()),
            _ => cutGeometry
        };
    }

    private static List<Coordinate[]> ExtractLinearCoordinateArrays(Geometry geometry)
    {
        var list = new List<Coordinate[]>();
        AppendLinearCoordinateArrays(geometry, list);
        return list;
    }

    private static void AppendLinearCoordinateArrays(Geometry geometry, List<Coordinate[]> list)
    {
        switch (geometry)
        {
            case Polygon poly:
                list.Add(poly.ExteriorRing.Coordinates);
                for (var i = 0; i < poly.NumInteriorRings; i++)
                {
                    list.Add(poly.GetInteriorRingN(i).Coordinates);
                }
                break;
            case LineString line:
                list.Add(line.Coordinates);
                break;
            case GeometryCollection coll:
                for (var i = 0; i < coll.NumGeometries; i++)
                {
                    AppendLinearCoordinateArrays(coll.GetGeometryN(i), list);
                }
                break;
            default:
                if (geometry.Coordinates.Length > 0)
                {
                    list.Add(geometry.Coordinates);
                }
                break;
        }
    }

    private static Polygon RebuildPolygonWithOrdinates(Polygon polygon, List<Coordinate[]> sourceRings, bool hasZ, bool hasM)
    {
        var shell = RebuildLinearRingWithOrdinates((LinearRing)polygon.ExteriorRing, sourceRings, hasZ, hasM);
        var holes = new LinearRing[polygon.NumInteriorRings];
        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            holes[i] = RebuildLinearRingWithOrdinates((LinearRing)polygon.GetInteriorRingN(i), sourceRings, hasZ, hasM);
        }

        return polygon.Factory.CreatePolygon(shell, holes);
    }

    private static LinearRing RebuildLinearRingWithOrdinates(LinearRing ring, List<Coordinate[]> sourceRings, bool hasZ, bool hasM)
        => ring.Factory.CreateLinearRing(RebuildCoordinates(ring.Coordinates, sourceRings, hasZ, hasM));

    private static LineString RebuildLineStringWithOrdinates(LineString line, List<Coordinate[]> sourceRings, bool hasZ, bool hasM)
        => line.Factory.CreateLineString(RebuildCoordinates(line.Coordinates, sourceRings, hasZ, hasM));

    private static Coordinate[] RebuildCoordinates(Coordinate[] coords, List<Coordinate[]> sourceRings, bool hasZ, bool hasM)
    {
        var result = new Coordinate[coords.Length];
        for (var i = 0; i < coords.Length; i++)
        {
            var (z, m) = InterpolateOrdinates(coords[i], sourceRings, hasZ, hasM);
            result[i] = (hasZ, hasM) switch
            {
                (true, true) => new CoordinateZM(coords[i].X, coords[i].Y, z, m),
                (true, false) => new CoordinateZ(coords[i].X, coords[i].Y, z),
                (false, true) => new CoordinateM(coords[i].X, coords[i].Y, m),
                _ => new Coordinate(coords[i].X, coords[i].Y)
            };
        }

        return result;
    }

    private static (double Z, double M) InterpolateOrdinates(Coordinate target, List<Coordinate[]> sourceRings, bool hasZ, bool hasM)
    {
        var z = target.Z;
        var m = target.M;
        if ((!hasZ || !double.IsNaN(z)) && (!hasM || !double.IsNaN(m)))
        {
            return (z, m);
        }

        // Exact or near-exact vertex match in shifted space
        foreach (var sourceCoords in sourceRings)
        {
            for (var i = 0; i < sourceCoords.Length; i++)
            {
                var src = sourceCoords[i];
                if (Math.Abs(src.X - target.X) <= 1e-9 && Math.Abs(src.Y - target.Y) <= 1e-9)
                {
                    if (hasZ && double.IsNaN(z)) z = src.Z;
                    if (hasM && double.IsNaN(m)) m = src.M;
                    return (z, m);
                }
            }
        }

        // Interpolate along intra-ring segments only (never across separate rings or parts)
        var bestDist = double.PositiveInfinity;
        var bestZ = z;
        var bestM = m;
        foreach (var sourceCoords in sourceRings)
        {
            for (var i = 0; i + 1 < sourceCoords.Length; i++)
            {
                var a = sourceCoords[i];
                var b = sourceCoords[i + 1];
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var lenSq = (dx * dx) + (dy * dy);
                if (lenSq <= double.Epsilon)
                {
                    continue;
                }

                var t = Math.Clamp((((target.X - a.X) * dx) + ((target.Y - a.Y) * dy)) / lenSq, 0d, 1d);
                var projX = a.X + (t * dx);
                var projY = a.Y + (t * dy);
                var dist = ((target.X - projX) * (target.X - projX)) + ((target.Y - projY) * (target.Y - projY));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    if (hasZ && double.IsNaN(z))
                    {
                        bestZ = !double.IsNaN(a.Z) && !double.IsNaN(b.Z) ? a.Z + (t * (b.Z - a.Z)) : (!double.IsNaN(a.Z) ? a.Z : b.Z);
                    }

                    if (hasM && double.IsNaN(m))
                    {
                        bestM = !double.IsNaN(a.M) && !double.IsNaN(b.M) ? a.M + (t * (b.M - a.M)) : (!double.IsNaN(a.M) ? a.M : b.M);
                    }
                }
            }
        }

        return (bestZ, bestM);
    }

    private sealed class UnshiftLongitudeFilter : ICoordinateSequenceFilter
    {
        public bool Done => false;

        public bool GeometryChanged { get; private set; }

        public void Filter(CoordinateSequence seq, int i)
        {
            seq.SetX(i, seq.GetX(i) - 360d);
            GeometryChanged = true;
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

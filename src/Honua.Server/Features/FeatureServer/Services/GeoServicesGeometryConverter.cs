// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Services;
using NetTopologySuite;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Shared helpers for GeoServices geometry conversions.
/// </summary>
internal static class GeoServicesGeometryConverter
{
    [ThreadStatic]
    private static WKBReader? _wkbReader;

    /// <summary>
    /// Converts WKB geometry to GeoServices format.
    /// </summary>
    public static GeoServicesGeometry? ConvertWkbToGeoServicesGeometry(
        byte[]? wkbGeometry,
        int? srid = null,
        Honua.Core.Configuration.GeometryLimits? geometryLimits = null,
        bool includeZ = true,
        bool includeM = true)
    {
        if (wkbGeometry == null || wkbGeometry.Length == 0)
            return null;

        var reader = GetWkbReader();
        Geometry geometry;

        try
        {
            geometry = reader.Read(wkbGeometry);
        }
        catch (Exception ex) when (ex is ParseException or FormatException)
        {
            return null;
        }

        if (geometry == null || geometry.IsEmpty)
        {
            return null;
        }

        if (geometryLimits != null)
        {
            geometry = GeometryOutputProcessor.ApplyLimits(geometry, geometryLimits) ?? geometry;
        }

        if (!includeZ || !includeM)
        {
            geometry = GeometryOutputProcessor.ApplyDimensionFilter(geometry, includeZ, includeM);
        }

        if (srid.HasValue)
        {
            geometry.SRID = srid.Value;
        }

        var spatialReference = srid.HasValue && srid.Value > 0
            ? new GeoServicesSpatialReference { Wkid = srid.Value, LatestWkid = srid.Value }
            : null;

        return ConvertGeometryToGeoServicesGeometry(geometry, spatialReference);
    }

    private static WKBReader GetWkbReader()
    {
        _wkbReader ??= new WKBReader();
        return _wkbReader;
    }

    /// <summary>
    /// Converts GeoServices geometry to WKB.
    /// </summary>
    public static byte[] ConvertGeoServicesGeometryToWkb(GeoServicesGeometry geometry, int? srid = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        if (IsEmptyGeometry(geometry))
        {
            throw new ArgumentException(UnsupportedGeometryMessage);
        }

        var targetSrid = srid
            ?? geometry.SpatialReference?.Wkid
            ?? geometry.SpatialReference?.LatestWkid;

        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(targetSrid ?? 0);
        var ntsGeometry = CreateGeometryFromGeoServices(geometry, factory)
            ?? throw new ArgumentException(UnsupportedGeometryMessage);

        if (targetSrid.HasValue)
        {
            ntsGeometry.SRID = targetSrid.Value;
        }

        var (hasZ, hasM) = GetHasZandM(ntsGeometry);
        var includeSrid = targetSrid.HasValue && targetSrid.Value > 0;
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: includeSrid, emitZ: hasZ, emitM: hasM);
        return writer.Write(ntsGeometry);
    }

    private static Geometry? CreateGeometryFromGeoServices(GeoServicesGeometry geometry, GeometryFactory factory)
    {
        if (geometry.Xmin.HasValue && geometry.Ymin.HasValue &&
            geometry.Xmax.HasValue && geometry.Ymax.HasValue)
        {
            var envelope = new Envelope(geometry.Xmin.Value, geometry.Xmax.Value, geometry.Ymin.Value, geometry.Ymax.Value);
            return factory.ToGeometry(envelope);
        }

        if (geometry.X.HasValue && geometry.Y.HasValue)
        {
            var ordinates = new List<double>(4) { geometry.X.Value, geometry.Y.Value };
            var hasZ = geometry.HasZ ?? geometry.Z.HasValue;
            var hasM = geometry.HasM ?? geometry.M.HasValue;

            if (hasZ && geometry.Z.HasValue)
            {
                ordinates.Add(geometry.Z.Value);
            }

            if (hasM && geometry.M.HasValue)
            {
                ordinates.Add(geometry.M.Value);
            }

            return factory.CreatePoint(CreateCoordinate(ordinates.ToArray(), hasZ, hasM));
        }

        if (geometry.Points is not null)
        {
            if (geometry.Points.Length == 0)
            {
                throw new ArgumentException("No valid points found in multipoint geometry");
            }

            var points = geometry.Points
                .Where(point => point is { Length: >= 2 })
                .Select(point => factory.CreatePoint(CreateCoordinate(point, geometry.HasZ, geometry.HasM)))
                .ToArray();

            if (points.Length == 0)
            {
                throw new ArgumentException("No valid points found in multipoint geometry");
            }

            return factory.CreateMultiPoint(points);
        }

        if (geometry.Paths is not null)
        {
            if (geometry.Paths.Length == 0)
            {
                throw new ArgumentException("No valid paths found in linestring geometry");
            }

            var lineStrings = geometry.Paths
                .Select(path => FilterValidPoints(path))
                .Select(path => CreateCoordinates(path, geometry.HasZ, geometry.HasM))
                .Where(coords => coords.Length >= 2)
                .Select(coords => factory.CreateLineString(coords))
                .ToArray();

            if (lineStrings.Length == 0)
            {
                throw new ArgumentException("No valid paths found in linestring geometry");
            }

            return lineStrings.Length == 1 ? lineStrings[0] : factory.CreateMultiLineString(lineStrings);
        }

        if (geometry.Rings is not null)
        {
            if (geometry.Rings.Length == 0)
            {
                throw new ArgumentException("No valid rings found in polygon geometry");
            }

            return CreatePolygonalGeometry(geometry.Rings, factory, geometry.HasZ, geometry.HasM);
        }

        return null;
    }

    private static Coordinate CreateCoordinate(double[] ordinates, bool? hasZ = null, bool? hasM = null)
    {
        if (hasZ == true && hasM == true)
        {
            if (ordinates.Length >= 4)
            {
                return new CoordinateZM(ordinates[0], ordinates[1], ordinates[2], ordinates[3]);
            }

            if (ordinates.Length >= 3)
            {
                return new CoordinateZ(ordinates[0], ordinates[1], ordinates[2]);
            }
        }

        if (hasZ == true && hasM != true)
        {
            if (ordinates.Length >= 3)
            {
                return new CoordinateZ(ordinates[0], ordinates[1], ordinates[2]);
            }

            return new Coordinate(ordinates[0], ordinates[1]);
        }

        if (hasM == true && hasZ != true)
        {
            if (ordinates.Length >= 3)
            {
                return new CoordinateM(ordinates[0], ordinates[1], ordinates[2]);
            }

            return new Coordinate(ordinates[0], ordinates[1]);
        }

        if (ordinates.Length > 3)
        {
            return new CoordinateZM(ordinates[0], ordinates[1], ordinates[2], ordinates[3]);
        }

        if (ordinates.Length > 2)
        {
            return new CoordinateZ(ordinates[0], ordinates[1], ordinates[2]);
        }

        return new Coordinate(ordinates[0], ordinates[1]);
    }

    private static Coordinate[] CreateCoordinates(double[][] points, bool? hasZ = null, bool? hasM = null)
    {
        var coords = new Coordinate[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            coords[i] = CreateCoordinate(points[i], hasZ, hasM);
        }

        return coords;
    }

    private static Geometry CreatePolygonalGeometry(double[][][] rings, GeometryFactory factory, bool? hasZ, bool? hasM)
    {
        var shells = new List<LinearRing>();
        var holes = new List<LinearRing>();
        var validRingCount = 0;

        foreach (var ring in rings)
        {
            var validRing = FilterValidPoints(ring);
            if (validRing.Length < 3)
            {
                continue;
            }

            var coords = CreateCoordinates(EnsureClosed(validRing), hasZ, hasM);
            if (coords.Length < 4)
            {
                continue;
            }

            var linearRing = factory.CreateLinearRing(coords);
            validRingCount++;

            if (Orientation.IsCCW(coords))
            {
                holes.Add(linearRing);
            }
            else
            {
                shells.Add(linearRing);
            }
        }

        if (validRingCount == 0)
        {
            throw new ArgumentException("No valid rings found in polygon geometry");
        }

        if (shells.Count == 0)
        {
            shells.AddRange(holes);
            holes.Clear();
        }

        var holeAssignments = new Dictionary<LinearRing, List<LinearRing>>();

        foreach (var hole in holes)
        {
            var holePoint = factory.CreatePoint(hole.Coordinate);
            LinearRing? assignedShell = null;

            foreach (var shell in shells)
            {
                var shellPolygon = factory.CreatePolygon(shell);
                if (shellPolygon.Covers(holePoint))
                {
                    assignedShell = shell;
                    break;
                }
            }

            if (assignedShell == null)
            {
                shells.Add(hole);
                continue;
            }

            if (!holeAssignments.TryGetValue(assignedShell, out var assignedHoles))
            {
                assignedHoles = new List<LinearRing>();
                holeAssignments[assignedShell] = assignedHoles;
            }

            assignedHoles.Add(hole);
        }

        if (holes.Count == 0 && shells.Count > 1)
        {
            var shellsToRemove = new List<LinearRing>();

            foreach (var candidate in shells)
            {
                var candidatePoint = factory.CreatePoint(candidate.Coordinate);
                LinearRing? containingShell = null;

                foreach (var shell in shells)
                {
                    if (ReferenceEquals(shell, candidate))
                    {
                        continue;
                    }

                    var shellPolygon = factory.CreatePolygon(shell);
                    if (shellPolygon.Covers(candidatePoint))
                    {
                        containingShell = shell;
                        break;
                    }
                }

                if (containingShell == null)
                {
                    continue;
                }

                if (!holeAssignments.TryGetValue(containingShell, out var assignedHoles))
                {
                    assignedHoles = new List<LinearRing>();
                    holeAssignments[containingShell] = assignedHoles;
                }

                assignedHoles.Add(candidate);
                shellsToRemove.Add(candidate);
            }

            if (shellsToRemove.Count != 0)
            {
                shells.RemoveAll(shellsToRemove.Contains);
            }
        }

        var polygons = new List<Polygon>();
        foreach (var shell in shells)
        {
            holeAssignments.TryGetValue(shell, out var assigned);
            polygons.Add(factory.CreatePolygon(shell, assigned?.ToArray() ?? Array.Empty<LinearRing>()));
        }

        return polygons.Count == 1 ? polygons[0] : factory.CreateMultiPolygon(polygons.ToArray());
    }

    private static double[][] EnsureClosed(double[][] ring)
    {
        if (ring.Length == 0)
        {
            return ring;
        }

        var first = ring[0];
        var last = ring[^1];
        if (first.Length >= 2 && last.Length >= 2 &&
            first[0].Equals(last[0]) && first[1].Equals(last[1]))
        {
            return ring;
        }

        var closed = new double[ring.Length + 1][];
        Array.Copy(ring, closed, ring.Length);
        closed[^1] = first;
        return closed;
    }

    private static double[][] FilterValidPoints(double[][]? points)
    {
        if (points == null || points.Length == 0)
        {
            return Array.Empty<double[]>();
        }

        var valid = new List<double[]>(points.Length);
        foreach (var point in points)
        {
            if (point is { Length: >= 2 })
            {
                valid.Add(point);
            }
        }

        return valid.Count == points.Length ? points : valid.ToArray();
    }

    private static GeoServicesGeometry? ConvertGeometryToGeoServicesGeometry(Geometry geometry, GeoServicesSpatialReference? spatialReference)
    {
        return geometry switch
        {
            Point point => ConvertPoint(point, spatialReference),
            MultiPoint multiPoint => ConvertMultiPoint(multiPoint, spatialReference),
            LineString lineString => ConvertLineString(lineString, spatialReference),
            MultiLineString multiLineString => ConvertMultiLineString(multiLineString, spatialReference),
            Polygon polygon => ConvertPolygon(polygon, spatialReference),
            MultiPolygon multiPolygon => ConvertMultiPolygon(multiPolygon, spatialReference),
            GeometryCollection collection => ConvertGeometryCollection(collection, spatialReference),
            _ => null
        };
    }

    private static GeoServicesGeometry ConvertPoint(Point point, GeoServicesSpatialReference? spatialReference)
    {
        var sequence = point.CoordinateSequence;
        var (hasZ, hasM) = GetHasZandM(point);
        var (z, m) = GetZandM(sequence, 0);

        return new GeoServicesGeometry
        {
            HasZ = hasZ,
            HasM = hasM,
            X = point.X,
            Y = point.Y,
            Z = z,
            M = m,
            SpatialReference = spatialReference
        };
    }

    private static GeoServicesGeometry ConvertMultiPoint(MultiPoint multiPoint, GeoServicesSpatialReference? spatialReference)
    {
        var (hasZ, hasM) = GetHasZandM(multiPoint);
        var points = new double[multiPoint.NumGeometries][];
        for (var i = 0; i < multiPoint.NumGeometries; i++)
        {
            var point = (Point)multiPoint.GetGeometryN(i);
            points[i] = BuildCoordinateArray(point.CoordinateSequence, 0);
        }

        return new GeoServicesGeometry { HasZ = hasZ, HasM = hasM, Points = points, SpatialReference = spatialReference };
    }

    private static GeoServicesGeometry ConvertLineString(LineString lineString, GeoServicesSpatialReference? spatialReference)
    {
        var (hasZ, hasM) = GetHasZandM(lineString);
        return new GeoServicesGeometry
        {
            HasZ = hasZ,
            HasM = hasM,
            Paths = [BuildLineCoordinates(lineString)],
            SpatialReference = spatialReference
        };
    }

    private static GeoServicesGeometry ConvertMultiLineString(MultiLineString multiLineString, GeoServicesSpatialReference? spatialReference)
    {
        var (hasZ, hasM) = GetHasZandM(multiLineString);
        var paths = new double[multiLineString.NumGeometries][][];
        for (var i = 0; i < multiLineString.NumGeometries; i++)
        {
            paths[i] = BuildLineCoordinates((LineString)multiLineString.GetGeometryN(i));
        }

        return new GeoServicesGeometry { HasZ = hasZ, HasM = hasM, Paths = paths, SpatialReference = spatialReference };
    }

    private static GeoServicesGeometry ConvertPolygon(Polygon polygon, GeoServicesSpatialReference? spatialReference)
    {
        var (hasZ, hasM) = GetHasZandM(polygon);
        return new GeoServicesGeometry
        {
            HasZ = hasZ,
            HasM = hasM,
            Rings = BuildPolygonRings(polygon).ToArray(),
            SpatialReference = spatialReference
        };
    }

    private static GeoServicesGeometry ConvertMultiPolygon(MultiPolygon multiPolygon, GeoServicesSpatialReference? spatialReference)
    {
        var (hasZ, hasM) = GetHasZandM(multiPolygon);
        var rings = new List<double[][]>();
        for (var i = 0; i < multiPolygon.NumGeometries; i++)
        {
            var polygon = (Polygon)multiPolygon.GetGeometryN(i);
            rings.AddRange(BuildPolygonRings(polygon));
        }

        return new GeoServicesGeometry { HasZ = hasZ, HasM = hasM, Rings = rings.ToArray(), SpatialReference = spatialReference };
    }

    private static GeoServicesGeometry? ConvertGeometryCollection(GeometryCollection collection, GeoServicesSpatialReference? spatialReference)
    {
        var points = new List<Point>();
        var lines = new List<LineString>();
        var polygons = new List<Polygon>();

        for (var i = 0; i < collection.NumGeometries; i++)
        {
            switch (collection.GetGeometryN(i))
            {
                case Point point:
                    points.Add(point);
                    break;
                case LineString lineString:
                    lines.Add(lineString);
                    break;
                case Polygon polygon:
                    polygons.Add(polygon);
                    break;
                case MultiPoint multiPoint:
                    points.AddRange(multiPoint.Geometries.Cast<Point>());
                    break;
                case MultiLineString multiLineString:
                    lines.AddRange(multiLineString.Geometries.Cast<LineString>());
                    break;
                case MultiPolygon multiPolygon:
                    polygons.AddRange(multiPolygon.Geometries.Cast<Polygon>());
                    break;
                default:
                    // Unsupported sub-geometry type; return null to let the caller handle gracefully
                    return null;
            }
        }

        if (polygons.Count > 0 && points.Count == 0 && lines.Count == 0)
        {
            return ConvertMultiPolygon(collection.Factory.CreateMultiPolygon(polygons.ToArray()), spatialReference);
        }

        if (lines.Count > 0 && points.Count == 0 && polygons.Count == 0)
        {
            return ConvertMultiLineString(collection.Factory.CreateMultiLineString(lines.ToArray()), spatialReference);
        }

        if (points.Count > 0 && lines.Count == 0 && polygons.Count == 0)
        {
            return ConvertMultiPoint(collection.Factory.CreateMultiPoint(points.ToArray()), spatialReference);
        }

        // Mixed geometry types cannot be represented in GeoServices JSON; return null
        // so the feature is still returned with its attributes but without geometry
        return null;
    }

    private static IEnumerable<double[][]> BuildPolygonRings(Polygon polygon)
    {
        if (polygon.ExteriorRing != null && !polygon.ExteriorRing.IsEmpty)
        {
            yield return BuildRingCoordinates(polygon.ExteriorRing, clockwise: true);
        }

        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            var interiorRing = polygon.GetInteriorRingN(i);
            if (interiorRing != null && !interiorRing.IsEmpty)
            {
                yield return BuildRingCoordinates(interiorRing, clockwise: false);
            }
        }
    }

    private static double[][] BuildRingCoordinates(LineString ring, bool clockwise)
    {
        var coords = BuildLineCoordinates(ring);
        if (coords.Length < 4)
        {
            return coords;
        }

        var isCcw = Orientation.IsCCW(ring.Coordinates);
        if (clockwise == isCcw)
        {
            Array.Reverse(coords);
        }

        return coords;
    }

    private static double[][] BuildLineCoordinates(LineString lineString)
    {
        var sequence = lineString.CoordinateSequence;
        var coords = new double[sequence.Count][];
        for (var i = 0; i < sequence.Count; i++)
        {
            coords[i] = BuildCoordinateArray(sequence, i);
        }

        return coords;
    }

    private static (double? z, double? m) GetZandM(CoordinateSequence sequence, int index)
    {
        double? z = null;
        double? m = null;

        var zValue = sequence.GetOrdinate(index, Ordinate.Z);
        if (!double.IsNaN(zValue))
        {
            z = zValue;
        }

        var mValue = sequence.GetOrdinate(index, Ordinate.M);
        if (!double.IsNaN(mValue))
        {
            m = mValue;
        }

        return (z, m);
    }

    private static (bool hasZ, bool hasM) GetHasZandM(Geometry geometry)
    {
        return Infrastructure.Services.GeometryService.DetectZMFromGeometry(geometry);
    }

    private static bool IsEmptyGeometry(GeoServicesGeometry geometry)
    {
        return !geometry.X.HasValue &&
            !geometry.Y.HasValue &&
            !geometry.Xmin.HasValue &&
            !geometry.Ymin.HasValue &&
            !geometry.Xmax.HasValue &&
            !geometry.Ymax.HasValue &&
            geometry.Points is null &&
            geometry.Paths is null &&
            geometry.Rings is null;
    }

    private const string UnsupportedGeometryMessage =
        "Invalid GeoServices JSON geometry format. Supported types: Point (x, y), Polygon (rings), LineString (paths), MultiPoint (points), Envelope (xmin, ymin, xmax, ymax)";

    private static double[] BuildCoordinateArray(CoordinateSequence sequence, int index)
    {
        var values = new List<double>(4)
        {
            sequence.GetX(index),
            sequence.GetY(index)
        };

        var z = sequence.GetOrdinate(index, Ordinate.Z);
        if (!double.IsNaN(z))
        {
            values.Add(z);
        }

        var m = sequence.GetOrdinate(index, Ordinate.M);
        if (!double.IsNaN(m))
        {
            values.Add(m);
        }

        return values.ToArray();
    }

}

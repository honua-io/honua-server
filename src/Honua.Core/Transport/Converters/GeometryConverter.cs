// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Transport.Converters;

/// <summary>
/// Converter for bidirectional conversion between NTS geometries and geospatial gRPC geometry messages.
/// Supports all standard OGC geometry types with full coordinate precision.
/// </summary>
public static class GeometryConverter
{
    private static readonly GeometryFactory _geometryFactory = new();
    private static readonly WKBReader _wkbReader = new();
    private static readonly WKBWriter _wkbWriter = new();

    /// <summary>
    /// Converts an NTS Geometry to a gRPC Geometry message.
    /// </summary>
    /// <param name="ntsGeometry">The NTS geometry to convert</param>
    /// <returns>gRPC geometry message</returns>
    /// <exception cref="NotSupportedException">Thrown for unsupported geometry types</exception>
    public static Geospatial.V1.Geometry ToGrpc(NetTopologySuite.Geometries.Geometry ntsGeometry)
    {
        return ntsGeometry switch
        {
            Point point => new Geospatial.V1.Geometry { Point = ConvertPoint(point) },
            MultiPoint multiPoint => new Geospatial.V1.Geometry { MultiPoint = ConvertMultiPoint(multiPoint) },
            LineString lineString => new Geospatial.V1.Geometry { Polyline = ConvertLineString(lineString) },
            MultiLineString multiLineString => new Geospatial.V1.Geometry { Polyline = ConvertMultiLineString(multiLineString) },
            Polygon polygon => new Geospatial.V1.Geometry { Polygon = ConvertPolygon(polygon) },
            MultiPolygon multiPolygon => new Geospatial.V1.Geometry { MultiPolygon = ConvertMultiPolygon(multiPolygon) },
            _ => throw new NotSupportedException($"Geometry type {ntsGeometry.GeometryType} is not supported")
        };
    }

    /// <summary>
    /// Converts a gRPC Geometry message to an NTS Geometry.
    /// </summary>
    /// <param name="grpcGeometry">The gRPC geometry message to convert</param>
    /// <returns>NTS geometry</returns>
    /// <exception cref="InvalidOperationException">Thrown when no geometry shape is set</exception>
    public static NetTopologySuite.Geometries.Geometry FromGrpc(Geospatial.V1.Geometry grpcGeometry)
    {
        return grpcGeometry.ShapeCase switch
        {
            Geospatial.V1.Geometry.ShapeOneofCase.Point => ConvertPoint(grpcGeometry.Point),
            Geospatial.V1.Geometry.ShapeOneofCase.MultiPoint => ConvertMultiPoint(grpcGeometry.MultiPoint),
            Geospatial.V1.Geometry.ShapeOneofCase.Polyline => ConvertPolyline(grpcGeometry.Polyline),
            Geospatial.V1.Geometry.ShapeOneofCase.Polygon => ConvertPolygon(grpcGeometry.Polygon),
            Geospatial.V1.Geometry.ShapeOneofCase.MultiPolygon => ConvertMultiPolygon(grpcGeometry.MultiPolygon),
            Geospatial.V1.Geometry.ShapeOneofCase.None => throw new InvalidOperationException("No geometry shape is set"),
            _ => throw new NotSupportedException($"Geometry shape {grpcGeometry.ShapeCase} is not supported")
        };
    }

    /// <summary>
    /// Converts Well-Known Binary (WKB) data to an NTS Geometry.
    /// </summary>
    /// <param name="wkb">The WKB byte array</param>
    /// <returns>NTS geometry</returns>
    public static NetTopologySuite.Geometries.Geometry FromWkb(byte[] wkb)
    {
        return _wkbReader.Read(wkb);
    }

    /// <summary>
    /// Converts an NTS Geometry to Well-Known Binary (WKB) data.
    /// </summary>
    /// <param name="ntsGeometry">The NTS geometry to convert</param>
    /// <returns>WKB byte array</returns>
    public static byte[] ToWkb(NetTopologySuite.Geometries.Geometry ntsGeometry)
    {
        return _wkbWriter.Write(ntsGeometry);
    }

    #region Point Conversion

    private static Geospatial.V1.PointGeometry ConvertPoint(NetTopologySuite.Geometries.Point ntsPoint)
    {
        var grpcPoint = new Geospatial.V1.PointGeometry
        {
            X = ntsPoint.X,
            Y = ntsPoint.Y
        };

        if (!double.IsNaN(ntsPoint.Z))
        {
            grpcPoint.Z = ntsPoint.Z;
        }

        if (ntsPoint.M is double m && !double.IsNaN(m))
        {
            grpcPoint.M = m;
        }

        return grpcPoint;
    }

    private static NetTopologySuite.Geometries.Point ConvertPoint(Geospatial.V1.PointGeometry grpcPoint)
    {
        var coordinate = new CoordinateZM(grpcPoint.X, grpcPoint.Y,
            grpcPoint.HasZ ? grpcPoint.Z : double.NaN,
            grpcPoint.HasM ? grpcPoint.M : double.NaN);

        return _geometryFactory.CreatePoint(coordinate);
    }

    #endregion

    #region MultiPoint Conversion

    private static Geospatial.V1.MultiPointGeometry ConvertMultiPoint(MultiPoint ntsMultiPoint)
    {
        var grpcMultiPoint = new Geospatial.V1.MultiPointGeometry();

        foreach (NetTopologySuite.Geometries.Point point in ntsMultiPoint.Geometries)
        {
            grpcMultiPoint.Points.Add(ConvertPoint(point));
        }

        return grpcMultiPoint;
    }

    private static MultiPoint ConvertMultiPoint(Geospatial.V1.MultiPointGeometry grpcMultiPoint)
    {
        var points = new NetTopologySuite.Geometries.Point[grpcMultiPoint.Points.Count];

        for (int i = 0; i < grpcMultiPoint.Points.Count; i++)
        {
            points[i] = ConvertPoint(grpcMultiPoint.Points[i]);
        }

        return _geometryFactory.CreateMultiPoint(points);
    }

    #endregion

    #region LineString/Polyline Conversion

    private static Geospatial.V1.PolylineGeometry ConvertLineString(LineString ntsLineString)
    {
        var grpcPolyline = new Geospatial.V1.PolylineGeometry();
        grpcPolyline.Paths.Add(ConvertCoordinateSequence(ntsLineString.CoordinateSequence));
        return grpcPolyline;
    }

    private static Geospatial.V1.PolylineGeometry ConvertMultiLineString(MultiLineString ntsMultiLineString)
    {
        var grpcPolyline = new Geospatial.V1.PolylineGeometry();

        foreach (LineString lineString in ntsMultiLineString.Geometries)
        {
            grpcPolyline.Paths.Add(ConvertCoordinateSequence(lineString.CoordinateSequence));
        }

        return grpcPolyline;
    }

    private static NetTopologySuite.Geometries.Geometry ConvertPolyline(Geospatial.V1.PolylineGeometry grpcPolyline)
    {
        if (grpcPolyline.Paths.Count == 1)
        {
            // Single LineString
            var coordinates = ConvertCoordinateSequence(grpcPolyline.Paths[0]);
            return _geometryFactory.CreateLineString(coordinates);
        }
        else
        {
            // MultiLineString
            var lineStrings = new LineString[grpcPolyline.Paths.Count];

            for (int i = 0; i < grpcPolyline.Paths.Count; i++)
            {
                var coordinates = ConvertCoordinateSequence(grpcPolyline.Paths[i]);
                lineStrings[i] = _geometryFactory.CreateLineString(coordinates);
            }

            return _geometryFactory.CreateMultiLineString(lineStrings);
        }
    }

    #endregion

    #region Polygon Conversion

    private static Geospatial.V1.PolygonGeometry ConvertPolygon(Polygon ntsPolygon)
    {
        var grpcPolygon = new Geospatial.V1.PolygonGeometry();

        // Exterior ring
        grpcPolygon.Rings.Add(ConvertCoordinateSequence(ntsPolygon.ExteriorRing.CoordinateSequence));

        // Interior rings (holes)
        for (int i = 0; i < ntsPolygon.NumInteriorRings; i++)
        {
            grpcPolygon.Rings.Add(ConvertCoordinateSequence(ntsPolygon.GetInteriorRingN(i).CoordinateSequence));
        }

        return grpcPolygon;
    }

    private static Polygon ConvertPolygon(Geospatial.V1.PolygonGeometry grpcPolygon)
    {
        if (grpcPolygon.Rings.Count == 0)
        {
            return _geometryFactory.CreatePolygon();
        }

        // Exterior ring
        var exteriorCoordinates = ConvertCoordinateSequence(grpcPolygon.Rings[0]);
        var exteriorRing = _geometryFactory.CreateLinearRing(exteriorCoordinates);

        // Interior rings (holes)
        LinearRing[]? holes = null;
        if (grpcPolygon.Rings.Count > 1)
        {
            holes = new LinearRing[grpcPolygon.Rings.Count - 1];
            for (int i = 1; i < grpcPolygon.Rings.Count; i++)
            {
                var holeCoordinates = ConvertCoordinateSequence(grpcPolygon.Rings[i]);
                holes[i - 1] = _geometryFactory.CreateLinearRing(holeCoordinates);
            }
        }

        return _geometryFactory.CreatePolygon(exteriorRing, holes);
    }

    #endregion

    #region MultiPolygon Conversion

    private static Geospatial.V1.MultiPolygonGeometry ConvertMultiPolygon(MultiPolygon ntsMultiPolygon)
    {
        var grpcMultiPolygon = new Geospatial.V1.MultiPolygonGeometry();

        foreach (Polygon polygon in ntsMultiPolygon.Geometries)
        {
            grpcMultiPolygon.Polygons.Add(ConvertPolygon(polygon));
        }

        return grpcMultiPolygon;
    }

    private static MultiPolygon ConvertMultiPolygon(Geospatial.V1.MultiPolygonGeometry grpcMultiPolygon)
    {
        var polygons = new Polygon[grpcMultiPolygon.Polygons.Count];

        for (int i = 0; i < grpcMultiPolygon.Polygons.Count; i++)
        {
            polygons[i] = ConvertPolygon(grpcMultiPolygon.Polygons[i]);
        }

        return _geometryFactory.CreateMultiPolygon(polygons);
    }

    #endregion

    #region Coordinate Sequence Conversion

    private static Geospatial.V1.CoordinateSequence ConvertCoordinateSequence(NetTopologySuite.Geometries.CoordinateSequence ntsCoords)
    {
        var grpcCoords = new Geospatial.V1.CoordinateSequence();

        for (int i = 0; i < ntsCoords.Count; i++)
        {
            var coord = new Geospatial.V1.Coordinate
            {
                X = ntsCoords.GetX(i),
                Y = ntsCoords.GetY(i)
            };

            if (ntsCoords.HasZ)
            {
                var z = ntsCoords.GetZ(i);
                if (!double.IsNaN(z))
                {
                    coord.Z = z;
                }
            }

            if (ntsCoords.HasM)
            {
                var m = ntsCoords.GetM(i);
                if (!double.IsNaN(m))
                {
                    coord.M = m;
                }
            }

            grpcCoords.Coords.Add(coord);
        }

        return grpcCoords;
    }

    private static NetTopologySuite.Geometries.Coordinate[] ConvertCoordinateSequence(Geospatial.V1.CoordinateSequence grpcCoords)
    {
        var ntsCoords = new NetTopologySuite.Geometries.Coordinate[grpcCoords.Coords.Count];

        for (int i = 0; i < grpcCoords.Coords.Count; i++)
        {
            var grpcCoord = grpcCoords.Coords[i];
            ntsCoords[i] = new CoordinateZM(
                grpcCoord.X,
                grpcCoord.Y,
                grpcCoord.HasZ ? grpcCoord.Z : double.NaN,
                grpcCoord.HasM ? grpcCoord.M : double.NaN
            );
        }

        return ntsCoords;
    }

    #endregion
}

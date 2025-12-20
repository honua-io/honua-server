// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Service for converting between geometry formats
/// </summary>
internal sealed class GeometryConverter : IGeometryConverter
{
    /// <summary>
    /// Converts Esri JSON geometry to Well-Known Binary (WKB) format
    /// </summary>
    /// <param name="esriJsonGeometry">Geometry in Esri JSON format</param>
    /// <returns>Geometry in WKB format</returns>
    /// <exception cref="ArgumentException">Thrown when geometry format is invalid</exception>
    public byte[] ConvertEsriJsonToWkb(string esriJsonGeometry)
    {
        try
        {
            using var jsonDoc = JsonDocument.Parse(esriJsonGeometry);
            var root = jsonDoc.RootElement;

            // Handle POINT geometry format
            if (root.TryGetProperty("x", out var xElement) && root.TryGetProperty("y", out var yElement))
            {
                return ConvertEsriPointToWkb(xElement.GetDouble(), yElement.GetDouble());
            }

            // Handle POLYGON/MULTIPOLYGON geometry format (rings)
            if (root.TryGetProperty("rings", out var ringsElement) && ringsElement.ValueKind == JsonValueKind.Array)
            {
                return ConvertEsriPolygonToWkb(ringsElement);
            }

            // Handle LINESTRING/POLYLINE geometry format (paths)
            if (root.TryGetProperty("paths", out var pathsElement) && pathsElement.ValueKind == JsonValueKind.Array)
            {
                return ConvertEsriLineStringToWkb(pathsElement);
            }

            // Handle MULTIPOINT geometry format (points)
            if (root.TryGetProperty("points", out var pointsElement) && pointsElement.ValueKind == JsonValueKind.Array)
            {
                return ConvertEsriMultiPointToWkb(pointsElement);
            }

            // Handle ENVELOPE/EXTENT geometry format
            if (root.TryGetProperty("xmin", out var xminElement) &&
                root.TryGetProperty("ymin", out var yminElement) &&
                root.TryGetProperty("xmax", out var xmaxElement) &&
                root.TryGetProperty("ymax", out var ymaxElement))
            {
                return ConvertEsriEnvelopeToWkb(
                    xminElement.GetDouble(),
                    yminElement.GetDouble(),
                    xmaxElement.GetDouble(),
                    ymaxElement.GetDouble());
            }

            throw new ArgumentException("Invalid Esri JSON geometry format. Supported types: Point (x, y), Polygon (rings), LineString (paths), MultiPoint (points), Envelope (xmin, ymin, xmax, ymax)");
        }
        catch (JsonException)
        {
            throw new ArgumentException("Invalid JSON format in geometry parameter");
        }
    }

    /// <summary>
    /// Converts Esri JSON polygon rings to WKB format
    /// </summary>
    private static byte[] ConvertEsriPolygonToWkb(JsonElement ringsElement)
    {
        var rings = new List<List<(double x, double y)>>();

        foreach (var ring in ringsElement.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array)
                continue;

            var points = new List<(double x, double y)>();
            foreach (var point in ring.EnumerateArray())
            {
                if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2)
                {
                    var x = point[0].GetDouble();
                    var y = point[1].GetDouble();
                    points.Add((x, y));
                }
            }
            if (points.Count > 0)
                rings.Add(points);
        }

        if (rings.Count == 0)
            throw new ArgumentException("No valid rings found in polygon geometry");

        // Create WKB for POLYGON geometry
        // WKB format: [endian][type][numRings][ring1][ring2]...
        // Each ring: [numPoints][point1][point2]...
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)1); // Little-endian
        writer.Write((uint)3); // POLYGON type

        writer.Write((uint)rings.Count); // Number of rings

        foreach (var ring in rings)
        {
            writer.Write((uint)ring.Count); // Number of points in ring
            foreach (var (x, y) in ring)
            {
                writer.Write(x); // X coordinate
                writer.Write(y); // Y coordinate
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Converts a point coordinate to WKB format
    /// </summary>
    private static byte[] ConvertEsriPointToWkb(double x, double y)
    {
        // Create WKB for a POINT geometry (little-endian format)
        // WKB format: [endian][type][x][y]
        var wkbBytes = new byte[21]; // 1 + 4 + 8 + 8 bytes
        wkbBytes[0] = 1; // Little-endian
        BitConverter.GetBytes((uint)1).CopyTo(wkbBytes, 1); // POINT type
        BitConverter.GetBytes(x).CopyTo(wkbBytes, 5); // X coordinate
        BitConverter.GetBytes(y).CopyTo(wkbBytes, 13); // Y coordinate

        return wkbBytes;
    }

    /// <summary>
    /// Converts Esri JSON linestring/polyline paths to WKB format
    /// </summary>
    private static byte[] ConvertEsriLineStringToWkb(JsonElement pathsElement)
    {
        var paths = new List<List<(double x, double y)>>();

        foreach (var path in pathsElement.EnumerateArray())
        {
            if (path.ValueKind != JsonValueKind.Array)
                continue;

            var points = new List<(double x, double y)>();
            foreach (var point in path.EnumerateArray())
            {
                if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2)
                {
                    var x = point[0].GetDouble();
                    var y = point[1].GetDouble();
                    points.Add((x, y));
                }
            }
            if (points.Count > 0)
                paths.Add(points);
        }

        if (paths.Count == 0)
            throw new ArgumentException("No valid paths found in linestring geometry");

        // For single path, create LINESTRING, for multiple paths create MULTILINESTRING
        if (paths.Count == 1)
        {
            return CreateLineStringWkb(paths[0]);
        }
        else
        {
            return CreateMultiLineStringWkb(paths);
        }
    }

    /// <summary>
    /// Converts Esri JSON multipoint to WKB format
    /// </summary>
    private static byte[] ConvertEsriMultiPointToWkb(JsonElement pointsElement)
    {
        var points = new List<(double x, double y)>();

        foreach (var point in pointsElement.EnumerateArray())
        {
            if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2)
            {
                var x = point[0].GetDouble();
                var y = point[1].GetDouble();
                points.Add((x, y));
            }
        }

        if (points.Count == 0)
            throw new ArgumentException("No valid points found in multipoint geometry");

        return CreateMultiPointWkb(points);
    }

    /// <summary>
    /// Converts Esri JSON envelope to WKB polygon format
    /// </summary>
    private static byte[] ConvertEsriEnvelopeToWkb(double xmin, double ymin, double xmax, double ymax)
    {
        // Convert envelope to a polygon (rectangle)
        var rectanglePoints = new List<(double x, double y)>
        {
            (xmin, ymin),  // bottom-left
            (xmax, ymin),  // bottom-right
            (xmax, ymax),  // top-right
            (xmin, ymax),  // top-left
            (xmin, ymin)   // close the ring
        };

        return CreatePolygonWkb([rectanglePoints]);
    }

    /// <summary>
    /// Creates WKB for a LINESTRING geometry
    /// </summary>
    private static byte[] CreateLineStringWkb(List<(double x, double y)> points)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)1); // Little-endian
        writer.Write((uint)2); // LINESTRING type

        writer.Write((uint)points.Count); // Number of points

        foreach (var (x, y) in points)
        {
            writer.Write(x); // X coordinate
            writer.Write(y); // Y coordinate
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Creates WKB for a MULTILINESTRING geometry
    /// </summary>
    private static byte[] CreateMultiLineStringWkb(List<List<(double x, double y)>> paths)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)1); // Little-endian
        writer.Write((uint)5); // MULTILINESTRING type

        writer.Write((uint)paths.Count); // Number of linestrings

        foreach (var path in paths)
        {
            writer.Write((byte)1); // Little-endian for nested linestring
            writer.Write((uint)2); // LINESTRING type
            writer.Write((uint)path.Count); // Number of points in this linestring

            foreach (var (x, y) in path)
            {
                writer.Write(x); // X coordinate
                writer.Write(y); // Y coordinate
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Creates WKB for a MULTIPOINT geometry
    /// </summary>
    private static byte[] CreateMultiPointWkb(List<(double x, double y)> points)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)1); // Little-endian
        writer.Write((uint)4); // MULTIPOINT type

        writer.Write((uint)points.Count); // Number of points

        foreach (var (x, y) in points)
        {
            writer.Write((byte)1); // Little-endian for nested point
            writer.Write((uint)1); // POINT type
            writer.Write(x); // X coordinate
            writer.Write(y); // Y coordinate
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Creates WKB for a POLYGON geometry
    /// </summary>
    private static byte[] CreatePolygonWkb(List<List<(double x, double y)>> rings)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)1); // Little-endian
        writer.Write((uint)3); // POLYGON type

        writer.Write((uint)rings.Count); // Number of rings

        foreach (var ring in rings)
        {
            writer.Write((uint)ring.Count); // Number of points in ring
            foreach (var (x, y) in ring)
            {
                writer.Write(x); // X coordinate
                writer.Write(y); // Y coordinate
            }
        }

        return stream.ToArray();
    }
}

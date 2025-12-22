// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using NetTopologySuite.IO;

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
            JsonElement root = jsonDoc.RootElement;

            // Handle POINT geometry format
            if (root.TryGetProperty("x", out JsonElement xElement) && root.TryGetProperty("y", out JsonElement yElement))
            {
                return ConvertEsriPointToWkb(xElement.GetDouble(), yElement.GetDouble());
            }

            // Handle POLYGON/MULTIPOLYGON geometry format (rings)
            if (root.TryGetProperty("rings", out JsonElement ringsElement) && ringsElement.ValueKind == JsonValueKind.Array)
            {
                return ConvertEsriPolygonToWkb(ringsElement);
            }

            // Handle LINESTRING/POLYLINE geometry format (paths)
            if (root.TryGetProperty("paths", out JsonElement pathsElement) && pathsElement.ValueKind == JsonValueKind.Array)
            {
                return ConvertEsriLineStringToWkb(pathsElement);
            }

            // Handle MULTIPOINT geometry format (points)
            if (root.TryGetProperty("points", out JsonElement pointsElement) && pointsElement.ValueKind == JsonValueKind.Array)
            {
                return ConvertEsriMultiPointToWkb(pointsElement);
            }

            // Handle ENVELOPE/EXTENT geometry format
            return root.TryGetProperty("xmin", out JsonElement xminElement) &&
                root.TryGetProperty("ymin", out JsonElement yminElement) &&
                root.TryGetProperty("xmax", out JsonElement xmaxElement) &&
                root.TryGetProperty("ymax", out JsonElement ymaxElement)
                ? ConvertEsriEnvelopeToWkb(
                    xminElement.GetDouble(),
                    yminElement.GetDouble(),
                    xmaxElement.GetDouble(),
                    ymaxElement.GetDouble())
                : throw new ArgumentException("Invalid Esri JSON geometry format. Supported types: Point (x, y), Polygon (rings), LineString (paths), MultiPoint (points), Envelope (xmin, ymin, xmax, ymax)");
        }
        catch (JsonException)
        {
            throw new ArgumentException("Invalid JSON format in geometry parameter");
        }
    }

    /// <summary>
    /// Converts Well-Known Binary (WKB) geometry to GeoJSON format
    /// </summary>
    /// <param name="wkbGeometry">Geometry in WKB format</param>
    /// <returns>Geometry in GeoJSON format as a JSON object</returns>
    /// <exception cref="ArgumentException">Thrown when WKB format is invalid</exception>
    public object? ConvertWkbToGeoJson(byte[] wkbGeometry)
    {
        if (wkbGeometry == null || wkbGeometry.Length == 0)
            return null;

        try
        {
            var reader = new WKBReader();
            var geometry = reader.Read(wkbGeometry);

            if (geometry == null)
                return null;

            var writer = new GeoJsonWriter();
            var geoJsonString = writer.Write(geometry);

            // Parse the GeoJSON string to return as an object
            using var jsonDoc = JsonDocument.Parse(geoJsonString);
            return JsonSerializer.Deserialize<JsonElement>(jsonDoc.RootElement.GetRawText());
        }
        catch (Exception ex) when (ex is ParseException or FormatException or JsonException)
        {
            throw new ArgumentException($"Invalid WKB geometry format: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Converts Esri JSON polygon rings to WKB format
    /// </summary>
    private static byte[] ConvertEsriPolygonToWkb(JsonElement ringsElement)
    {
        var rings = new List<List<(double x, double y)>>();

        foreach (JsonElement ring in ringsElement.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array)
                continue;

            var points = new List<(double x, double y)>();
            foreach (JsonElement point in ring.EnumerateArray())
            {
                if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2)
                {
                    double x = point[0].GetDouble();
                    double y = point[1].GetDouble();
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

        foreach (List<(double x, double y)> ring in rings)
        {
            writer.Write((uint)ring.Count); // Number of points in ring
            foreach ((double x, double y) in ring)
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
        byte[] wkbBytes = new byte[21]; // 1 + 4 + 8 + 8 bytes
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

        foreach (JsonElement path in pathsElement.EnumerateArray())
        {
            if (path.ValueKind != JsonValueKind.Array)
                continue;

            var points = new List<(double x, double y)>();
            foreach (JsonElement point in path.EnumerateArray())
            {
                if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2)
                {
                    double x = point[0].GetDouble();
                    double y = point[1].GetDouble();
                    points.Add((x, y));
                }
            }
            if (points.Count > 0)
                paths.Add(points);
        }

        if (paths.Count == 0)
            throw new ArgumentException("No valid paths found in linestring geometry");

        // For single path, create LINESTRING, for multiple paths create MULTILINESTRING
        return paths.Count == 1 ? CreateLineStringWkb(paths[0]) : CreateMultiLineStringWkb(paths);
    }

    /// <summary>
    /// Converts Esri JSON multipoint to WKB format
    /// </summary>
    private static byte[] ConvertEsriMultiPointToWkb(JsonElement pointsElement)
    {
        var points = new List<(double x, double y)>();

        foreach (JsonElement point in pointsElement.EnumerateArray())
        {
            if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2)
            {
                double x = point[0].GetDouble();
                double y = point[1].GetDouble();
                points.Add((x, y));
            }
        }

        return points.Count == 0 ? throw new ArgumentException("No valid points found in multipoint geometry") : CreateMultiPointWkb(points);
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

        foreach ((double x, double y) in points)
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

        foreach (List<(double x, double y)> path in paths)
        {
            writer.Write((byte)1); // Little-endian for nested linestring
            writer.Write((uint)2); // LINESTRING type
            writer.Write((uint)path.Count); // Number of points in this linestring

            foreach ((double x, double y) in path)
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

        foreach ((double x, double y) in points)
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

        foreach (List<(double x, double y)> ring in rings)
        {
            writer.Write((uint)ring.Count); // Number of points in ring
            foreach ((double x, double y) in ring)
            {
                writer.Write(x); // X coordinate
                writer.Write(y); // Y coordinate
            }
        }

        return stream.ToArray();
    }
}

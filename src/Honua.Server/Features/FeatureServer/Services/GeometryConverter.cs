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
        // TODO: Implement full Esri JSON to WKB conversion using PostGIS
        // For MVP, we'll support basic point and polygon geometries for testing

        try
        {
            using var jsonDoc = JsonDocument.Parse(esriJsonGeometry);
            var root = jsonDoc.RootElement;

            // Handle POINT geometry format
            if (root.TryGetProperty("x", out var xElement) && root.TryGetProperty("y", out var yElement))
            {
                var x = xElement.GetDouble();
                var y = yElement.GetDouble();

                // Create WKB for a POINT geometry (little-endian format)
                // WKB format: [endian][type][x][y]
                var wkbBytes = new byte[21]; // 1 + 4 + 8 + 8 bytes
                wkbBytes[0] = 1; // Little-endian
                BitConverter.GetBytes((uint)1).CopyTo(wkbBytes, 1); // POINT type
                BitConverter.GetBytes(x).CopyTo(wkbBytes, 5); // X coordinate
                BitConverter.GetBytes(y).CopyTo(wkbBytes, 13); // Y coordinate

                return wkbBytes;
            }

            // Handle POLYGON geometry format
            if (root.TryGetProperty("rings", out var ringsElement) && ringsElement.ValueKind == JsonValueKind.Array)
            {
                return ConvertEsriPolygonToWkb(ringsElement);
            }

            throw new ArgumentException("Invalid Esri JSON geometry format. Supported types: Point (x, y), Polygon (rings)");
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
}

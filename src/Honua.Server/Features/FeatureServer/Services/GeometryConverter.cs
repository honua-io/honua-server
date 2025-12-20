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
        // For MVP, we'll create a simple point geometry for testing

        try
        {
            // Parse basic Esri JSON point geometry format
            using var jsonDoc = JsonDocument.Parse(esriJsonGeometry);
            var root = jsonDoc.RootElement;

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

            throw new ArgumentException("Invalid Esri JSON geometry format");
        }
        catch (JsonException)
        {
            throw new ArgumentException("Invalid JSON format in geometry parameter");
        }
    }
}

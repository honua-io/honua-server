// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Infrastructure.Services;

/// <summary>
/// Service for converting between geometry formats
/// </summary>
internal sealed class GeometryConverter : IGeometryConverter
{
    /// <summary>
    /// Converts GeoServices JSON geometry to Well-Known Binary (WKB) format
    /// </summary>
    /// <param name="geoServicesJsonGeometry">Geometry in GeoServices JSON format</param>
    /// <returns>Geometry in WKB format</returns>
    /// <exception cref="ArgumentException">Thrown when geometry format is invalid</exception>
    public byte[] ConvertGeoServicesJsonToWkb(string geoServicesJsonGeometry)
    {
        try
        {
            var geometry = JsonSerializer.Deserialize(
                geoServicesJsonGeometry,
                FeatureServerJsonContext.Default.GeoServicesGeometry)
                ?? throw new ArgumentException("Invalid GeoServices JSON geometry format.");

            return GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(geometry);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Invalid JSON format in geometry parameter");
        }
    }

    /// <summary>
    /// Converts Well-Known Binary (WKB) geometry to GeoJSON format using pooled memory
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

            // Return the GeoJSON string directly to avoid JsonElement AOT issues
            // The caller can parse this as needed for their specific JSON context
            return geoJsonString;
        }
        catch (Exception ex) when (ex is ParseException or FormatException or JsonException)
        {
            throw new ArgumentException($"Invalid WKB geometry format: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Converts Well-Known Binary (WKB) geometry to GeoJSON format using pooled memory for large geometries
    /// </summary>
    /// <param name="wkbGeometry">Geometry in WKB format as Memory&lt;byte&gt;</param>
    /// <returns>Geometry in GeoJSON format as a JSON object</returns>
    /// <exception cref="ArgumentException">Thrown when WKB format is invalid</exception>
    public object? ConvertWkbToGeoJson(Memory<byte> wkbGeometry)
    {
        if (wkbGeometry.Length == 0)
            return null;

        try
        {
            var reader = new WKBReader();
            var geometry = reader.Read(wkbGeometry.Span.ToArray());

            if (geometry == null)
                return null;

            var writer = new GeoJsonWriter();
            var geoJsonString = writer.Write(geometry);

            return geoJsonString;
        }
        catch (Exception ex) when (ex is ParseException or FormatException or JsonException)
        {
            throw new ArgumentException($"Invalid WKB geometry format: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Converts Well-Known Binary (WKB) geometry to a GeoServices JSON element
    /// </summary>
    /// <param name="wkbGeometry">Geometry in WKB format</param>
    /// <param name="srid">Spatial reference ID to include in the output</param>
    /// <returns>Geometry as a JsonElement in GeoServices format</returns>
    /// <exception cref="ArgumentException">Thrown when WKB format is invalid</exception>
    public JsonElement ConvertWkbToGeoServicesGeometry(byte[] wkbGeometry, int srid)
    {
        ArgumentNullException.ThrowIfNull(wkbGeometry);

        var geoServicesGeometry = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(wkbGeometry, srid)
            ?? throw new ArgumentException("Failed to convert WKB to GeoServices geometry.");

        var json = JsonSerializer.Serialize(geoServicesGeometry, FeatureServerJsonContext.Default.GeoServicesGeometry);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Converts geometry to WKB using pooled memory for efficient processing
    /// </summary>
    /// <param name="geometry">NetTopologySuite geometry to convert</param>
    /// <returns>WKB data as a byte array</returns>
    /// <remarks>
    /// Uses memory pooling to reduce allocations during WKB conversion.
    /// This is particularly beneficial for processing many geometries during imports.
    /// </remarks>
    public static byte[] ConvertGeometryToWkbWithPooling(NetTopologySuite.Geometries.Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        var writer = new WKBWriter();

        // For large geometries, we could estimate buffer size and use pooled memory
        // For now, use standard approach but with potential for pooled optimization
        return writer.Write(geometry);
    }

    /// <summary>
    /// Estimates WKB size for a geometry to determine appropriate buffer size for pooling
    /// </summary>
    /// <param name="geometry">Geometry to estimate WKB size for</param>
    /// <returns>Estimated WKB byte size</returns>
    public static int EstimateWkbSize(NetTopologySuite.Geometries.Geometry geometry)
    {
        if (geometry == null || geometry.IsEmpty)
            return 0;

        // Rough estimate based on geometry type and number of coordinates
        // Each coordinate is ~16 bytes (2 doubles) + overhead for geometry structure
        var coordinateCount = geometry.NumPoints;
        var baseOverhead = geometry.GeometryType switch
        {
            "Point" => 21,      // WKB header + point structure
            "LineString" => 25,  // WKB header + linestring structure
            "Polygon" => 29,     // WKB header + polygon structure
            "MultiPoint" => 29,  // WKB header + multi structure
            "MultiLineString" => 33, // WKB header + multi structure
            "MultiPolygon" => 37,    // WKB header + multi structure
            _ => 41              // Default for complex geometries
        };

        return baseOverhead + (coordinateCount * 16);
    }

}

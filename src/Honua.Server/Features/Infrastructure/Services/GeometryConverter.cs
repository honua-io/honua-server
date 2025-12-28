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

}

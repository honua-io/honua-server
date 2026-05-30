// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.OData.Models;

namespace Honua.Protocols.OData.Services;

/// <summary>
/// Converts between OData spatial payloads and WKB geometries.
/// </summary>
internal static class ODataGeometryConverter
{
    private const string InvalidGeometryPayloadMessage = "Invalid geometry payload.";

    public static ODataSpatialGeometry? ConvertWkbToGeometry(
        IGeometryService geometryService,
        byte[]? wkb,
        int? srid,
        AxisOrder axisOrder)
    {
        if (wkb == null || wkb.Length == 0)
        {
            return null;
        }

        var geoJson = geometryService.ConvertWkbToGeoJson(wkb);
        if (string.IsNullOrWhiteSpace(geoJson))
        {
            return null;
        }

        _ = axisOrder; // OData uses GeoJSON coordinate order regardless of CRS axis order.

        using var document = JsonDocument.Parse(geoJson);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString() ?? "Geometry";

        string? coordinatesJson = null;
        string? geometriesJson = null;

        if (root.TryGetProperty("coordinates", out var coordinates))
        {
            coordinatesJson = coordinates.GetRawText();
        }

        if (root.TryGetProperty("geometries", out var geometries))
        {
            geometriesJson = geometries.GetRawText();
        }

        var resolvedSrid = srid ?? geometryService.GetGeometryInfo(wkb)?.Srid;

        return new ODataSpatialGeometry
        {
            Type = type,
            CoordinatesJson = coordinatesJson,
            GeometriesJson = geometriesJson,
            Crs = BuildCrs(resolvedSrid)
        };
    }

    public static GeometryConversionResult ConvertGeometryToWkb(
        IGeometryService geometryService,
        ODataSpatialGeometry? geometry,
        int defaultSrid,
        CrsDefinition? crsDefinition)
    {
        if (geometry == null)
        {
            return GeometryConversionResult.Success(null);
        }

        if (string.IsNullOrWhiteSpace(geometry.Type))
        {
            return GeometryConversionResult.Failure("Geometry type is required.");
        }

        if (string.IsNullOrWhiteSpace(geometry.CoordinatesJson) && string.IsNullOrWhiteSpace(geometry.GeometriesJson))
        {
            return GeometryConversionResult.Failure("Geometry coordinates are required.");
        }

        if (geometry.Crs != null)
        {
            if (!crsDefinition.HasValue)
            {
                return GeometryConversionResult.Failure("Unsupported geometry CRS.");
            }
        }

        var srid = crsDefinition?.Srid ?? defaultSrid;
        var geoJson = BuildGeoJson(geometry);

        try
        {
            var wkb = geometryService.ConvertGeoJsonToWkb(geoJson, srid);
            return GeometryConversionResult.Success(wkb);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return GeometryConversionResult.Failure(InvalidGeometryPayloadMessage);
        }
    }

    private static string BuildGeoJson(ODataSpatialGeometry geometry)
    {
        if (!string.IsNullOrWhiteSpace(geometry.CoordinatesJson))
        {
            return $"{{\"type\":\"{geometry.Type}\",\"coordinates\":{geometry.CoordinatesJson}}}";
        }

        return $"{{\"type\":\"{geometry.Type}\",\"geometries\":{geometry.GeometriesJson}}}";
    }

    private static ODataSpatialCrs? BuildCrs(int? srid)
    {
        if (!srid.HasValue || srid.Value <= 0)
        {
            return null;
        }

        if (srid.Value == SpatialReference.WGS84.Wkid)
        {
            return null;
        }

        return new ODataSpatialCrs
        {
            Type = "name",
            Properties = new ODataSpatialCrsProperties
            {
                Name = $"EPSG:{srid.Value}"
            }
        };
    }

    internal readonly record struct GeometryConversionResult
    {
        public bool IsSuccess { get; init; }
        public byte[]? Wkb { get; init; }
        public string? ErrorMessage { get; init; }

        public static GeometryConversionResult Success(byte[]? wkb) => new() { IsSuccess = true, Wkb = wkb };
        public static GeometryConversionResult Failure(string errorMessage) => new() { IsSuccess = false, ErrorMessage = errorMessage };
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Converts between OData spatial payloads and WKB geometries.
/// </summary>
internal static class ODataGeometryConverter
{
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

        if (axisOrder == AxisOrder.NorthEast)
        {
            geoJson = SwapGeoJsonAxisOrder(geoJson);
        }

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
        var axisOrder = crsDefinition?.AxisOrder ?? AxisOrder.EastNorth;
        var geoJson = BuildGeoJson(geometry);
        if (axisOrder == AxisOrder.NorthEast)
        {
            geoJson = SwapGeoJsonAxisOrder(geoJson);
        }

        try
        {
            var wkb = geometryService.ConvertGeoJsonToWkb(geoJson, srid);
            return GeometryConversionResult.Success(wkb);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return GeometryConversionResult.Failure(ex.Message);
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

    private static string SwapGeoJsonAxisOrder(string geoJson)
    {
        using var document = JsonDocument.Parse(geoJson);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteSwappedElement(document.RootElement, writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSwappedElement(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteSwappedElement(property.Value, writer);
                }
                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                if (IsCoordinateArray(element))
                {
                    WriteSwappedCoordinateArray(element, writer);
                    return;
                }

                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSwappedElement(item, writer);
                }
                writer.WriteEndArray();
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }

    private static bool IsCoordinateArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        if (element.GetArrayLength() < 2)
        {
            return false;
        }

        var first = element[0];
        var second = element[1];
        return first.ValueKind == JsonValueKind.Number &&
               second.ValueKind == JsonValueKind.Number;
    }

    private static void WriteSwappedCoordinateArray(JsonElement element, Utf8JsonWriter writer)
    {
        var items = element.EnumerateArray().ToArray();
        writer.WriteStartArray();
        if (items.Length >= 2)
        {
            items[1].WriteTo(writer);
            items[0].WriteTo(writer);
            for (var i = 2; i < items.Length; i++)
            {
                items[i].WriteTo(writer);
            }
        }
        else
        {
            foreach (var item in items)
            {
                item.WriteTo(writer);
            }
        }
        writer.WriteEndArray();
    }
}

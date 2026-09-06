// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing.Execution;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>Formats canonical artifacts using the Esri GP value contract.</summary>
internal static class GPServerEsriOutputTranslation
{
    private const string GeoJsonPrefix = "data:application/geo+json;base64,";

    public static JsonElement Translate(ArtifactKind kind, string value, int srid, string? schemaJson = null)
    {
        using var schema = schemaJson is null ? null : JsonDocument.Parse(schemaJson);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (kind is ArtifactKind.FeatureLayer or ArtifactKind.Table && value.StartsWith(GeoJsonPrefix, StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(Convert.FromBase64String(value[GeoJsonPrefix.Length..]));
                WriteFeatureSet(writer, document.RootElement, srid > 0 ? srid : 4326, schema?.RootElement);
            }
            else if (kind is ArtifactKind.FeatureLayer or ArtifactKind.Table or ArtifactKind.File or ArtifactKind.Report
                or ArtifactKind.Map or ArtifactKind.AppBundle or ArtifactKind.Raster)
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                    uri.Scheme is "http" or "https" or "data")
                {
                    writer.WriteStartObject();
                    writer.WriteString("url", value);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteStringValue(value);
                }
            }
            else
            {
                writer.WriteStringValue(value);
            }
        }
        using var result = JsonDocument.Parse(buffer.ToArray());
        return result.RootElement.Clone();
    }

    public static JsonElement DescribeInput(string canonicalValue, string? declaredInput)
    {
        var inferred = Translate(ArtifactKind.FeatureLayer, canonicalValue, 4326);
        using var declared = declaredInput?.TrimStart().StartsWith('{') == true
            ? JsonDocument.Parse(declaredInput) : null;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in inferred.EnumerateObject())
            {
                if (property.Name is not ("features" or "fields"))
                {
                    property.WriteTo(writer);
                }
            }
            if (!inferred.TryGetProperty("geometryType", out _) &&
                declared?.RootElement.TryGetProperty("geometryType", out var type) == true)
            {
                writer.WritePropertyName("geometryType");
                type.WriteTo(writer);
            }
            writer.WriteStartArray("fields");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in inferred.GetProperty("fields").EnumerateArray())
            {
                names.Add(field.GetProperty("name").GetString()!);
                field.WriteTo(writer);
            }
            if (declared?.RootElement.TryGetProperty("fields", out var fields) == true)
            {
                foreach (var field in fields.EnumerateArray())
                {
                    var name = field.GetProperty("name").GetString()!;
                    if (names.Add(name))
                    {
                        var fieldType = field.GetProperty("type").GetString()!;
                        WriteField(writer, name, fieldType == "esriFieldTypeOID" ? "esriFieldTypeInteger" : fieldType);
                    }
                }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var result = JsonDocument.Parse(buffer.ToArray());
        return result.RootElement.Clone();
    }

    private static void WriteFeatureSet(Utf8JsonWriter writer, JsonElement root, int srid, JsonElement? schema)
    {
        var features = root.GetProperty("type").GetString() == "FeatureCollection"
            ? root.GetProperty("features").EnumerateArray().ToArray()
            : [root];
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            if (feature.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    var type = property.Value.ValueKind switch
                    {
                        JsonValueKind.Number => "esriFieldTypeDouble",
                        JsonValueKind.True or JsonValueKind.False => "esriFieldTypeSmallInteger",
                        _ => "esriFieldTypeString"
                    };
                    if (!fields.ContainsKey(property.Name) || property.Value.ValueKind != JsonValueKind.Null)
                    {
                        fields[property.Name] = type;
                    }
                }
            }
        }
        if (features.Length == 0 && schema is { } declared && declared.TryGetProperty("fields", out var declaredFields))
        {
            var syntheticOid = declared.TryGetProperty("objectIdFieldName", out var oid) ? oid.GetString() : null;
            foreach (var field in declaredFields.EnumerateArray())
            {
                var name = field.GetProperty("name").GetString()!;
                if (name != syntheticOid)
                {
                    fields[name] = field.GetProperty("type").GetString()!;
                }
            }
        }
        var oidName = "OBJECTID";
        while (fields.Keys.Contains(oidName, StringComparer.OrdinalIgnoreCase))
        {
            oidName = "_" + oidName;
        }

        var geometries = new List<GeoServicesGeometry?>(features.Length);
        string? geometryType = features.Length == 0 && schema is { } shape && shape.TryGetProperty("geometryType", out var declaredType)
            ? declaredType.GetString() : null;
        var hasZ = features.Length == 0 && schema is { } zSchema && zSchema.TryGetProperty("hasZ", out var z) && z.ValueKind == JsonValueKind.True;
        var hasM = features.Length == 0 && schema is { } mSchema && mSchema.TryGetProperty("hasM", out var m) && m.ValueKind == JsonValueKind.True;
        foreach (var feature in features)
        {
            GeoServicesGeometry? esri = null;
            if (feature.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.Object)
            {
                var nts = GeoJsonArtifactCodec.CreateReader().Read<Geometry>(geometry.GetRawText());
                var type = nts.OgcGeometryType switch
                {
                    OgcGeometryType.Point => "esriGeometryPoint",
                    OgcGeometryType.MultiPoint => "esriGeometryMultipoint",
                    OgcGeometryType.LineString or OgcGeometryType.MultiLineString => "esriGeometryPolyline",
                    OgcGeometryType.Polygon or OgcGeometryType.MultiPolygon => "esriGeometryPolygon",
                    _ => throw new ArgumentException("GP output contains an unsupported geometry collection.")
                };
                if (geometryType is not null && geometryType != type)
                {
                    throw new ArgumentException("GP FeatureSet outputs must have a single geometry type.");
                }
                geometryType = type;
                hasZ |= nts.Coordinates.Any(coordinate => !double.IsNaN(coordinate.Z));
                hasM |= nts.Coordinates.Any(coordinate => !double.IsNaN(coordinate.M));
                esri = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(
                    new WKBWriter(ByteOrder.LittleEndian, true,
                        nts.Coordinates.Any(coordinate => !double.IsNaN(coordinate.Z)),
                        nts.Coordinates.Any(coordinate => !double.IsNaN(coordinate.M))).Write(nts), srid > 0 ? srid : null);
            }
            geometries.Add(esri);
        }

        writer.WriteStartObject();
        writer.WriteString("objectIdFieldName", oidName);
        if (geometryType is not null)
        {
            writer.WriteString("geometryType", geometryType);
            writer.WriteBoolean("hasZ", hasZ);
            writer.WriteBoolean("hasM", hasM);
        }
        if (srid > 0)
        {
            writer.WriteStartObject("spatialReference");
            writer.WriteNumber("wkid", srid);
            writer.WriteEndObject();
        }
        writer.WriteStartArray("fields");
        WriteField(writer, oidName, "esriFieldTypeOID");
        foreach (var field in fields)
        {
            WriteField(writer, field.Key, field.Value);
        }
        writer.WriteEndArray();
        writer.WriteStartArray("features");
        for (var index = 0; index < features.Length; index++)
        {
            writer.WriteStartObject();
            writer.WriteStartObject("attributes");
            writer.WriteNumber(oidName, index + 1);
            if (features[index].TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        writer.WriteNumber(property.Name, property.Value.GetBoolean() ? 1 : 0);
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }
            }
            writer.WriteEndObject();
            writer.WritePropertyName("geometry");
            JsonSerializer.Serialize(writer, geometries[index], FeatureServerJsonContext.Default.GeoServicesGeometry);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteField(Utf8JsonWriter writer, string name, string type)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("alias", name);
        writer.WriteString("type", type);
        writer.WriteEndObject();
    }
}

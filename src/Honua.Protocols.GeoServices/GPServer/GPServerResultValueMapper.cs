// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Protocols.GeoServices.GPServer.Models;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>Maps internal GP artifacts to the value shapes ArcGIS clients consume.</summary>
internal static class GPServerResultValueMapper
{
    public static JsonElement Map(ArtifactKind kind, string value, bool isLocation)
    {
        if (kind == ArtifactKind.FeatureLayer && TryReadGeoJson(value, out var featureSet))
        {
            return featureSet;
        }

        if (isLocation || Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return JsonSerializer.SerializeToElement(
                new GPResultUrlValue { Url = value },
                GPServerJsonContext.Default.GPResultUrlValue);
        }

        return JsonSerializer.SerializeToElement(value);
    }

    private static bool TryReadGeoJson(string value, out JsonElement featureSet)
    {
        featureSet = default;
        const string prefix = "data:application/geo+json;base64,";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value[prefix.Length..]));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            JsonElement features;
            if (root.TryGetProperty("features", out var collection))
            {
                features = collection;
            }
            else if (root.TryGetProperty("type", out var type) && type.GetString() == "Feature")
            {
                features = JsonSerializer.SerializeToElement(new[] { root });
            }
            else return false;

            var outputFeatures = new JsonArray();
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? geometryType = null;
            foreach (var feature in features.EnumerateArray())
            {
                var attributes = feature.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
                    ? JsonNode.Parse(properties.GetRawText())!.AsObject()
                    : new JsonObject();
                foreach (var property in attributes) fields.TryAdd(property.Key, ToEsriFieldType(property.Value));

                JsonNode? geometry = null;
                if (feature.TryGetProperty("geometry", out var geo) && geo.ValueKind == JsonValueKind.Object)
                {
                    geometry = ToEsriGeometry(geo, out var currentType);
                    geometryType ??= currentType;
                }

                var esriFeature = new JsonObject { ["attributes"] = attributes };
                if (geometry is not null) esriFeature["geometry"] = geometry;
                outputFeatures.Add(esriFeature);
            }

            var output = new JsonObject
            {
                ["displayFieldName"] = string.Empty,
                ["geometryType"] = geometryType ?? "esriGeometryPoint",
                ["fields"] = new JsonArray(fields.Select(field => (JsonNode)new JsonObject
                {
                    ["name"] = field.Key,
                    ["alias"] = field.Key,
                    ["type"] = field.Value
                }).ToArray()),
                ["features"] = outputFeatures
            };
            featureSet = JsonSerializer.SerializeToElement(output);
            return true;
        }
        catch (FormatException) { return false; }
        catch (JsonException) { return false; }
    }

    private static JsonObject? ToEsriGeometry(JsonElement geometry, out string geometryType)
    {
        geometryType = "esriGeometryPoint";
        if (!geometry.TryGetProperty("type", out var type) || !geometry.TryGetProperty("coordinates", out var coordinates)) return null;
        switch (type.GetString())
        {
            case "Point":
                return new JsonObject { ["x"] = coordinates[0].GetDouble(), ["y"] = coordinates[1].GetDouble() };
            case "MultiPoint":
                geometryType = "esriGeometryMultipoint";
                return new JsonObject { ["points"] = JsonNode.Parse(coordinates.GetRawText()) };
            case "LineString":
                geometryType = "esriGeometryPolyline";
                return new JsonObject { ["paths"] = new JsonArray(JsonNode.Parse(coordinates.GetRawText())) };
            case "MultiLineString":
                geometryType = "esriGeometryPolyline";
                return new JsonObject { ["paths"] = JsonNode.Parse(coordinates.GetRawText()) };
            case "Polygon":
                geometryType = "esriGeometryPolygon";
                return new JsonObject { ["rings"] = JsonNode.Parse(coordinates.GetRawText()) };
            case "MultiPolygon":
                geometryType = "esriGeometryPolygon";
                return new JsonObject { ["rings"] = FlattenPolygons(coordinates) };
            default:
                return null;
        }
    }

    private static JsonArray FlattenPolygons(JsonElement polygons)
    {
        var rings = new JsonArray();
        foreach (var polygon in polygons.EnumerateArray())
            foreach (var ring in polygon.EnumerateArray()) rings.Add(JsonNode.Parse(ring.GetRawText()));
        return rings;
    }

    private static string ToEsriFieldType(JsonNode? value)
        => value switch
        {
            JsonValue jsonValue when jsonValue.TryGetValue<long>(out _) => "esriFieldTypeInteger",
            JsonValue jsonValue when jsonValue.TryGetValue<double>(out _) => "esriFieldTypeDouble",
            JsonValue jsonValue when jsonValue.TryGetValue<bool>(out _) => "esriFieldTypeSmallInteger",
            _ => "esriFieldTypeString"
        };
}

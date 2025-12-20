// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.FeatureServer.Models;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Service for formatting query results into different output formats
/// </summary>
public interface IQueryFormatter
{
    /// <summary>
    /// Formats query result into the specified format
    /// </summary>
    /// <param name="result">Query result with features</param>
    /// <param name="layer">Layer definition for metadata</param>
    /// <param name="format">Output format (json, geojson)</param>
    /// <param name="returnGeometry">Whether to include geometry</param>
    /// <param name="outFields">Fields to include in output</param>
    /// <returns>Formatted result and content type</returns>
    (object response, string contentType) FormatQueryResult(
        QueryResult<Feature> result,
        LayerDefinition layer,
        string format,
        bool returnGeometry,
        string[]? outFields = null);
}

/// <summary>
/// Implementation of query formatter service
/// </summary>
public sealed class QueryFormatter : IQueryFormatter
{
    /// <summary>
    /// Formats query result into the specified format
    /// </summary>
    public (object response, string contentType) FormatQueryResult(
        QueryResult<Feature> result,
        LayerDefinition layer,
        string format,
        bool returnGeometry,
        string[]? outFields = null)
    {
        return format.ToLowerInvariant() switch
        {
            "geojson" => FormatAsGeoJson(result, layer, returnGeometry, outFields),
            "json" or _ => FormatAsEsriJson(result, layer, returnGeometry, outFields)
        };
    }

    /// <summary>
    /// Formats result as Esri JSON
    /// </summary>
    private (object response, string contentType) FormatAsEsriJson(
        QueryResult<Feature> result,
        LayerDefinition layer,
        bool returnGeometry,
        string[]? outFields)
    {
        var features = result.Items.Select(f => ConvertToEsriFeature(f, returnGeometry, outFields)).ToArray();

        var response = new QueryResponse
        {
            ObjectIdFieldName = layer.PrimaryKeyField?.Name ?? "objectid",
            Features = features,
            ExceededTransferLimit = result.HasMoreResults
        };

        return (response, "application/json");
    }

    /// <summary>
    /// Formats result as GeoJSON
    /// </summary>
    private (object response, string contentType) FormatAsGeoJson(
        QueryResult<Feature> result,
        LayerDefinition layer,
        bool returnGeometry,
        string[]? outFields)
    {
        var features = result.Items.Select(f => ConvertToGeoJsonFeature(f, returnGeometry, outFields)).ToArray();

        var response = new GeoJsonFeatureSet
        {
            Features = features,
            Properties = new Dictionary<string, object?>
            {
                ["objectIdFieldName"] = layer.PrimaryKeyField?.Name ?? "objectid",
                ["exceededTransferLimit"] = result.HasMoreResults,
                ["totalFeatures"] = result.TotalCount
            }
        };

        return (response, "application/geo+json");
    }

    /// <summary>
    /// Converts a Feature to Esri feature format
    /// </summary>
    private static EsriFeature ConvertToEsriFeature(Feature feature, bool returnGeometry, string[]? outFields)
    {
        var attributes = FilterAttributes(feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value), outFields);

        return new EsriFeature
        {
            Attributes = attributes,
            Geometry = returnGeometry ? ConvertGeometryToEsriFormat(feature.Geometry) : null
        };
    }

    /// <summary>
    /// Converts a Feature to GeoJSON feature format
    /// </summary>
    private static GeoJsonFeature ConvertToGeoJsonFeature(Feature feature, bool returnGeometry, string[]? outFields)
    {
        var properties = FilterAttributes(feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value), outFields);

        // Extract the ID from attributes if available
        // Normalize numeric values to ensure type consistency
        object? id = null;
        if (properties.TryGetValue("objectid", out var objectId))
        {
            // Normalize numeric types to avoid JsonElement vs primitive mismatches
            id = objectId switch
            {
                System.Text.Json.JsonElement jsonElement when jsonElement.ValueKind == System.Text.Json.JsonValueKind.Number =>
                    jsonElement.TryGetInt64(out var longVal) ? longVal : (object)jsonElement.GetDouble(),
                _ => objectId
            };
        }
        else if (properties.TryGetValue("id", out var idValue))
        {
            id = idValue switch
            {
                System.Text.Json.JsonElement jsonElement when jsonElement.ValueKind == System.Text.Json.JsonValueKind.Number =>
                    jsonElement.TryGetInt64(out var longVal) ? longVal : (object)jsonElement.GetDouble(),
                _ => idValue
            };
        }

        return new GeoJsonFeature
        {
            Properties = properties,
            Geometry = returnGeometry ? ConvertGeometryToGeoJsonFormat(feature.Geometry) : null,
            Id = id
        };
    }

    /// <summary>
    /// Filters attributes based on outFields parameter
    /// </summary>
    private static Dictionary<string, object?> FilterAttributes(
        Dictionary<string, object?> attributes,
        string[]? outFields)
    {
        if (outFields == null || outFields.Length == 0)
            return attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var filtered = new Dictionary<string, object?>();

        // Always include objectid field for Esri compatibility
        if (attributes.TryGetValue("objectid", out var objectIdValue))
            filtered["objectid"] = objectIdValue;

        foreach (var field in outFields)
        {
            if (attributes.TryGetValue(field, out var fieldValue))
                filtered[field] = fieldValue;
        }

        return filtered;
    }

    /// <summary>
    /// Converts WKB geometry to Esri JSON format (simplified for testing)
    /// </summary>
    private static EsriGeometry? ConvertGeometryToEsriFormat(byte[]? wkbGeometry)
    {
        if (wkbGeometry == null || wkbGeometry.Length < 21)
            return null;

        // Only support little-endian WKB point geometries for now
        if (wkbGeometry[0] != 1)
        {
            return null;
        }

        var geometryType = BitConverter.ToUInt32(wkbGeometry, 1);
        if (geometryType != 1)
        {
            return null;
        }

        var x = BitConverter.ToDouble(wkbGeometry, 5);  // X coordinate at offset 5
        var y = BitConverter.ToDouble(wkbGeometry, 13); // Y coordinate at offset 13

        return new EsriGeometry
        {
            X = x,
            Y = y,
            SpatialReference = new EsriSpatialReference { Wkid = 4326 }
        };
    }

    /// <summary>
    /// Converts WKB geometry to GeoJSON format
    /// </summary>
    private static GeoJsonGeometry? ConvertGeometryToGeoJsonFormat(byte[]? wkbGeometry)
    {
        if (wkbGeometry == null || wkbGeometry.Length < 21)
            return null;

        // Only support little-endian WKB point geometries for now
        if (wkbGeometry[0] != 1)
        {
            return null;
        }

        var geometryType = BitConverter.ToUInt32(wkbGeometry, 1);
        if (geometryType != 1)
        {
            return null;
        }

        var x = BitConverter.ToDouble(wkbGeometry, 5);  // X coordinate at offset 5
        var y = BitConverter.ToDouble(wkbGeometry, 13); // Y coordinate at offset 13

        return new GeoJsonGeometry
        {
            Type = "Point",
            Coordinates = new[] { x, y },
            Crs = new GeoJsonCrs
            {
                Properties = new Dictionary<string, object>
                {
                    ["name"] = "EPSG:4326"
                }
            }
        };
    }
}

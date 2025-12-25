// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.FeatureServer.Models;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Service for formatting query results into different output formats
/// </summary>
internal interface IQueryFormatter
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
internal sealed class QueryFormatter : IQueryFormatter
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
            "json" or _ => FormatAsGeoServicesJson(result, layer, returnGeometry, outFields)
        };
    }

    /// <summary>
    /// Formats result as GeoServices JSON
    /// </summary>
    private static (object response, string contentType) FormatAsGeoServicesJson(
        QueryResult<Feature> result,
        LayerDefinition layer,
        bool returnGeometry,
        string[]? outFields)
    {
        GeoServicesFeature[] features = result.Items.Select(f => ConvertToGeoServicesFeature(f, returnGeometry, outFields)).ToArray();

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
    private static (object response, string contentType) FormatAsGeoJson(
        QueryResult<Feature> result,
        LayerDefinition layer,
        bool returnGeometry,
        string[]? outFields)
    {
        GeoJsonFeature[] features = result.Items.Select(f => ConvertToGeoJsonFeature(f, returnGeometry, outFields)).ToArray();

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
    /// Converts a Feature to GeoServices feature format
    /// </summary>
    private static GeoServicesFeature ConvertToGeoServicesFeature(Feature feature, bool returnGeometry, string[]? outFields)
    {
        Dictionary<string, object?> attributes = FilterAttributes(feature.Attributes, outFields);

        return new GeoServicesFeature
        {
            Attributes = attributes,
            Geometry = returnGeometry ? GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(feature.Geometry) : null
        };
    }

    /// <summary>
    /// Converts a Feature to GeoJSON feature format
    /// </summary>
    private static GeoJsonFeature ConvertToGeoJsonFeature(Feature feature, bool returnGeometry, string[]? outFields)
    {
        Dictionary<string, object?> properties = FilterAttributes(feature.Attributes, outFields);

        // Extract the ID from attributes if available
        // Normalize numeric values to ensure type consistency
        object? id = null;
        if (properties.TryGetValue("objectid", out object? objectId))
        {
            // Normalize numeric types to avoid JsonElement vs primitive mismatches
            id = objectId switch
            {
                System.Text.Json.JsonElement jsonElement when jsonElement.ValueKind == System.Text.Json.JsonValueKind.Number =>
                    jsonElement.TryGetInt64(out long longVal) ? longVal : (object)jsonElement.GetDouble(),
                _ => objectId
            };
        }
        else if (properties.TryGetValue("id", out object? idValue))
        {
            id = idValue switch
            {
                System.Text.Json.JsonElement jsonElement when jsonElement.ValueKind == System.Text.Json.JsonValueKind.Number =>
                    jsonElement.TryGetInt64(out long longVal) ? longVal : (object)jsonElement.GetDouble(),
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
        ImmutableDictionary<string, object?> attributes,
        string[]? outFields)
    {
        if (outFields == null || outFields.Length == 0)
            return attributes is Dictionary<string, object?> dict
                ? new Dictionary<string, object?>(dict)
                : attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var filtered = new Dictionary<string, object?>();

        // Always include objectid field for GeoServices compatibility
        if (attributes.TryGetValue("objectid", out object? objectIdValue))
            filtered["objectid"] = objectIdValue;

        foreach (string field in outFields)
        {
            if (attributes.TryGetValue(field, out object? fieldValue))
                filtered[field] = fieldValue;
        }

        return filtered;
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

        uint geometryType = BitConverter.ToUInt32(wkbGeometry, 1);
        if (geometryType != 1)
        {
            return null;
        }

        double x = BitConverter.ToDouble(wkbGeometry, 5);  // X coordinate at offset 5
        double y = BitConverter.ToDouble(wkbGeometry, 13); // Y coordinate at offset 13

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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Services;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NtsGeometryType = NetTopologySuite.IO.GeometryType;

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
    /// <param name="outputSrid">Output SRID for geometry</param>
    /// <param name="returnZ">Whether to include Z values</param>
    /// <param name="returnM">Whether to include M values</param>
    /// <param name="geometryPrecision">Coordinate precision override</param>
    /// <param name="maxAllowableOffset">Generalization tolerance override</param>
    /// <param name="outFields">Fields to include in output</param>
    /// <returns>Formatted result and content type</returns>
    (object response, string contentType) FormatQueryResult(
        QueryResult<Feature> result,
        LayerDefinition layer,
        string format,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        int? geometryPrecision,
        double? maxAllowableOffset,
        string[]? outFields = null);
}

/// <summary>
/// Implementation of query formatter service
/// </summary>
internal sealed class QueryFormatter : IQueryFormatter
{
    private readonly GeometryLimits _geometryLimits;

    public QueryFormatter(IOptions<LimitsOptions> limitsOptions)
    {
        _geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
    }

    /// <summary>
    /// Formats query result into the specified format
    /// </summary>
    public (object response, string contentType) FormatQueryResult(
        QueryResult<Feature> result,
        LayerDefinition layer,
        string format,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        int? geometryPrecision,
        double? maxAllowableOffset,
        string[]? outFields = null)
    {
        var effectiveLimits = GeometryOutputProcessor.CreateEffectiveLimits(
            _geometryLimits,
            geometryPrecision,
            maxAllowableOffset,
            forceSimplify: maxAllowableOffset is > 0);

        return format.ToLowerInvariant() switch
        {
            "geojson" => FormatAsGeoJson(result, layer, returnGeometry, returnZ, returnM, effectiveLimits, outFields),
            "json" or _ => FormatAsGeoServicesJson(result, layer, returnGeometry, outputSrid, returnZ, returnM, effectiveLimits, outFields)
        };
    }

    /// <summary>
    /// Formats result as GeoServices JSON
    /// </summary>
    private (object response, string contentType) FormatAsGeoServicesJson(
        QueryResult<Feature> result,
        LayerDefinition layer,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        string[]? outFields)
    {
        var objectIdFieldName = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;
        GeoServicesFeature[] features = result.Items
            .Select(f => ConvertToGeoServicesFeature(f, returnGeometry, outFields, objectIdFieldName, returnZ, returnM, geometryLimits))
            .ToArray();

        var srid = outputSrid ?? layer.SpatialReference.Wkid;
        var spatialReference = layer.HasGeometry
            ? new GeoServicesSpatialReference { Wkid = srid, LatestWkid = srid }
            : null;

        var response = new QueryResponse
        {
            ObjectIdFieldName = objectIdFieldName,
            GeometryType = layer.HasGeometry ? MapGeometryType(layer.GeometryType) : null,
            SpatialReference = spatialReference,
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
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        string[]? outFields)
    {
        var objectIdFieldName = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;
        GeoJsonFeature[] features = result.Items
            .Select(f => ConvertToGeoJsonFeature(f, returnGeometry, outFields, objectIdFieldName, returnZ, returnM, geometryLimits))
            .ToArray();

        var response = new GeoJsonFeatureSet
        {
            Features = features,
            Properties = new Dictionary<string, object?>
            {
                ["objectIdFieldName"] = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId,
                ["exceededTransferLimit"] = result.HasMoreResults,
                ["totalFeatures"] = result.TotalCount
            }
        };

        return (response, "application/geo+json");
    }

    /// <summary>
    /// Converts a Feature to GeoServices feature format
    /// </summary>
    private GeoServicesFeature ConvertToGeoServicesFeature(
        Feature feature,
        bool returnGeometry,
        string[]? outFields,
        string objectIdFieldName,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits)
    {
        Dictionary<string, object?> attributes = FilterAttributes(feature.Attributes, outFields, objectIdFieldName, feature.Id);

        return new GeoServicesFeature
        {
            Attributes = attributes,
            Geometry = returnGeometry
                ? GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(
                    feature.Geometry,
                    null,
                    geometryLimits,
                    returnZ,
                    returnM)
                : null
        };
    }

    /// <summary>
    /// Converts a Feature to GeoJSON feature format
    /// </summary>
    private GeoJsonFeature ConvertToGeoJsonFeature(
        Feature feature,
        bool returnGeometry,
        string[]? outFields,
        string objectIdFieldName,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits)
    {
        Dictionary<string, object?> properties = FilterAttributes(feature.Attributes, outFields, objectIdFieldName, feature.Id);

        // Extract the ID from attributes if available
        // Normalize numeric values to ensure type consistency
        object? id = null;
        if (properties.TryGetValue(FieldNames.ObjectId, out object? objectId))
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

        if (id == null)
        {
            id = feature.Id;
        }

        return new GeoJsonFeature
        {
            Properties = properties,
            Geometry = returnGeometry ? ConvertGeometryToGeoJsonFormat(feature.Geometry, geometryLimits, returnZ, returnM) : null,
            Id = id
        };
    }

    /// <summary>
    /// Filters attributes based on outFields parameter
    /// </summary>
    private static Dictionary<string, object?> FilterAttributes(
        ImmutableDictionary<string, object?> attributes,
        string[]? outFields,
        string objectIdFieldName,
        long objectIdValue)
    {
        if (outFields == null || outFields.Length == 0 ||
            (outFields.Length == 1 && outFields[0].Equals("*", StringComparison.Ordinal)))
        {
            var all = attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            if (!all.ContainsKey(objectIdFieldName))
            {
                all[objectIdFieldName] = objectIdValue;
            }
            AddObjectIdAlias(all, objectIdFieldName);
            return all;
        }

        var filtered = new Dictionary<string, object?>();

        // Always include objectid field for GeoServices compatibility
        if (attributes.TryGetValue(objectIdFieldName, out object? objectIdFromAttributes))
        {
            filtered[objectIdFieldName] = objectIdFromAttributes;
        }
        else
        {
            filtered[objectIdFieldName] = objectIdValue;
        }
        foreach (string field in outFields)
        {
            if (attributes.TryGetValue(field, out object? fieldValue))
                filtered[field] = fieldValue;
        }

        return filtered;
    }

    private static void AddObjectIdAlias(Dictionary<string, object?> target, string objectIdFieldName)
    {
        if (!objectIdFieldName.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (target.ContainsKey("OBJECTID"))
        {
            return;
        }

        if (target.TryGetValue(objectIdFieldName, out var objectIdValue))
        {
            target["OBJECTID"] = objectIdValue;
        }
    }

    /// <summary>
    /// Converts WKB geometry to GeoJSON format
    /// </summary>
    internal static GeoJsonGeometry? ConvertGeometryToGeoJsonFormat(
        byte[]? wkbGeometry,
        GeometryLimits geometryLimits,
        bool returnZ,
        bool returnM)
    {
        if (wkbGeometry == null || wkbGeometry.Length == 0)
            return null;

        var reader = new WKBReader();
        Geometry geometry;

        try
        {
            geometry = reader.Read(wkbGeometry);
        }
        catch (Exception ex) when (ex is ParseException or FormatException)
        {
            return null;
        }

        if (geometry == null || geometry.IsEmpty)
        {
            return null;
        }

        geometry = GeometryOutputProcessor.ApplyLimits(geometry, geometryLimits) ?? geometry;
        if (!returnZ || !returnM)
        {
            geometry = GeometryOutputProcessor.ApplyDimensionFilter(geometry, returnZ, returnM);
        }
        return ConvertGeometryToGeoJsonGeometry(geometry);
    }

    private static GeoJsonGeometry ConvertGeometryToGeoJsonGeometry(Geometry geometry)
    {
        if (geometry is GeometryCollection collection)
        {
            return new GeoJsonGeometry
            {
                Type = "GeometryCollection",
                Coordinates = null,
                Geometries = collection.Geometries.Select(ConvertGeometryToGeoJsonGeometry).ToArray()
            };
        }

        return new GeoJsonGeometry
        {
            Type = MapGeoJsonType(geometry),
            Coordinates = BuildGeoJsonCoordinates(geometry)
        };
    }

    private static string MapGeoJsonType(Geometry geometry)
    {
        return geometry.OgcGeometryType switch
        {
            OgcGeometryType.Point => "Point",
            OgcGeometryType.LineString => "LineString",
            OgcGeometryType.Polygon => "Polygon",
            OgcGeometryType.MultiPoint => "MultiPoint",
            OgcGeometryType.MultiLineString => "MultiLineString",
            OgcGeometryType.MultiPolygon => "MultiPolygon",
            _ => geometry.GeometryType
        };
    }

    private static object BuildGeoJsonCoordinates(Geometry geometry)
    {
        return geometry switch
        {
            Point point => BuildPointCoordinates(point),
            LineString lineString => BuildLineStringCoordinates(lineString),
            Polygon polygon => BuildPolygonCoordinates(polygon),
            MultiPoint multiPoint => BuildMultiPointCoordinates(multiPoint),
            MultiLineString multiLineString => BuildMultiLineStringCoordinates(multiLineString),
            MultiPolygon multiPolygon => BuildMultiPolygonCoordinates(multiPolygon),
            _ => throw new ArgumentException($"Unsupported geometry type: {geometry.GeometryType}")
        };
    }

    private static double[] BuildPointCoordinates(Point point)
    {
        var sequence = point.CoordinateSequence;
        return BuildCoordinate(sequence, 0);
    }

    private static double[][] BuildLineStringCoordinates(LineString lineString)
    {
        var sequence = lineString.CoordinateSequence;
        var coords = new double[sequence.Count][];
        for (var i = 0; i < sequence.Count; i++)
        {
            coords[i] = BuildCoordinate(sequence, i);
        }

        return coords;
    }

    private static double[][][] BuildPolygonCoordinates(Polygon polygon)
    {
        var rings = new List<double[][]>
        {
            BuildLineStringCoordinates(polygon.ExteriorRing)
        };

        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            rings.Add(BuildLineStringCoordinates(polygon.GetInteriorRingN(i)));
        }

        return rings.ToArray();
    }

    private static double[][] BuildMultiPointCoordinates(MultiPoint multiPoint)
    {
        var coords = new double[multiPoint.NumGeometries][];
        for (var i = 0; i < multiPoint.NumGeometries; i++)
        {
            coords[i] = BuildPointCoordinates((Point)multiPoint.GetGeometryN(i));
        }

        return coords;
    }

    private static double[][][] BuildMultiLineStringCoordinates(MultiLineString multiLineString)
    {
        var lines = new double[multiLineString.NumGeometries][][];
        for (var i = 0; i < multiLineString.NumGeometries; i++)
        {
            lines[i] = BuildLineStringCoordinates((LineString)multiLineString.GetGeometryN(i));
        }

        return lines;
    }

    private static double[][][][] BuildMultiPolygonCoordinates(MultiPolygon multiPolygon)
    {
        var polygons = new double[multiPolygon.NumGeometries][][][];
        for (var i = 0; i < multiPolygon.NumGeometries; i++)
        {
            polygons[i] = BuildPolygonCoordinates((Polygon)multiPolygon.GetGeometryN(i));
        }

        return polygons;
    }

    private static double[] BuildCoordinate(CoordinateSequence sequence, int index)
    {
        var values = new List<double>(4)
        {
            sequence.GetX(index),
            sequence.GetY(index)
        };

        if (sequence.Dimension > 2)
        {
            var z = sequence.GetOrdinate(index, Ordinate.Z);
            if (!double.IsNaN(z))
            {
                values.Add(z);
            }
        }

        if (sequence.Measures > 0)
        {
            var m = sequence.GetOrdinate(index, Ordinate.M);
            if (!double.IsNaN(m))
            {
                values.Add(m);
            }
        }

        return values.ToArray();
    }

    private static string MapGeometryType(Honua.Core.Features.Catalog.Domain.GeometryType geometryType)
    {
        return geometryType switch
        {
            Honua.Core.Features.Catalog.Domain.GeometryType.Point => "esriGeometryPoint",
            Honua.Core.Features.Catalog.Domain.GeometryType.LineString => "esriGeometryPolyline",
            Honua.Core.Features.Catalog.Domain.GeometryType.Polygon => "esriGeometryPolygon",
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiPoint => "esriGeometryMultipoint",
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiLineString => "esriGeometryPolyline",
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiPolygon => "esriGeometryPolygon",
            Honua.Core.Features.Catalog.Domain.GeometryType.GeometryCollection => "esriGeometryNull",
            Honua.Core.Features.Catalog.Domain.GeometryType.None => "esriGeometryNull",
            _ => "esriGeometryPolygon"
        };
    }
}

/// <summary>
/// Streaming query formatter that writes JSON responses incrementally to reduce memory pressure
/// </summary>
internal sealed class StreamingQueryFormatter
{
    private readonly ILogger<StreamingQueryFormatter> _logger;
    private readonly GeometryLimits _geometryLimits;

    public StreamingQueryFormatter(ILogger<StreamingQueryFormatter> logger, IOptions<LimitsOptions> limitsOptions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
    }

    /// <summary>
    /// Streams query result as GeoServices JSON format using Utf8JsonWriter
    /// </summary>
    public async Task StreamAsGeoServicesJsonAsync(
        IAsyncEnumerable<Feature> features,
        LayerDefinition layer,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        int? geometryPrecision,
        double? maxAllowableOffset,
        string[]? outFields,
        long totalCount,
        bool hasMoreResults,
        PipeWriter outputStream,
        CancellationToken cancellationToken = default)
    {
        using var writer = new Utf8JsonWriter(outputStream, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });

        var objectIdFieldName = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;
        var srid = outputSrid ?? layer.SpatialReference.Wkid;

        // Start object
        writer.WriteStartObject();

        // Write metadata
        writer.WriteString("objectIdFieldName", objectIdFieldName);

        if (layer.HasGeometry)
        {
            writer.WriteString("geometryType", MapGeometryType(layer.GeometryType));
            writer.WriteStartObject("spatialReference");
            writer.WriteNumber("wkid", srid);
            writer.WriteNumber("latestWkid", srid);
            writer.WriteEndObject();
        }

        var effectiveLimits = GeometryOutputProcessor.CreateEffectiveLimits(
            _geometryLimits,
            geometryPrecision,
            maxAllowableOffset,
            forceSimplify: maxAllowableOffset is > 0);

        // Start features array
        writer.WriteStartArray("features");

        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            await WriteGeoServicesFeatureAsync(
                writer,
                feature,
                returnGeometry,
                outFields,
                objectIdFieldName,
                returnZ,
                returnM,
                effectiveLimits,
                cancellationToken);

            // Flush periodically to improve streaming performance
            await writer.FlushAsync(cancellationToken);
        }

        // End features array
        writer.WriteEndArray();

        // Write additional metadata
        writer.WriteBoolean("exceededTransferLimit", hasMoreResults);

        // End object
        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Streams query result as GeoJSON format using Utf8JsonWriter
    /// </summary>
    public async Task StreamAsGeoJsonAsync(
        IAsyncEnumerable<Feature> features,
        LayerDefinition layer,
        bool returnGeometry,
        bool returnZ,
        bool returnM,
        int? geometryPrecision,
        double? maxAllowableOffset,
        string[]? outFields,
        long totalCount,
        bool hasMoreResults,
        PipeWriter outputStream,
        CancellationToken cancellationToken = default)
    {
        using var writer = new Utf8JsonWriter(outputStream, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });

        var objectIdFieldName = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;

        // Start GeoJSON FeatureCollection
        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");

        // Start features array
        writer.WriteStartArray("features");

        var effectiveLimits = GeometryOutputProcessor.CreateEffectiveLimits(
            _geometryLimits,
            geometryPrecision,
            maxAllowableOffset,
            forceSimplify: maxAllowableOffset is > 0);

        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            await WriteGeoJsonFeatureAsync(
                writer,
                feature,
                returnGeometry,
                outFields,
                objectIdFieldName,
                returnZ,
                returnM,
                effectiveLimits,
                cancellationToken);

            // Flush periodically to improve streaming performance
            await writer.FlushAsync(cancellationToken);
        }

        // End features array
        writer.WriteEndArray();

        // Write properties with metadata
        writer.WriteStartObject("properties");
        writer.WriteString("objectIdFieldName", objectIdFieldName);
        writer.WriteBoolean("exceededTransferLimit", hasMoreResults);
        writer.WriteNumber("totalFeatures", totalCount);
        writer.WriteEndObject();

        // End FeatureCollection
        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes a single feature in GeoServices format
    /// </summary>
    private async Task WriteGeoServicesFeatureAsync(
        Utf8JsonWriter writer,
        Feature feature,
        bool returnGeometry,
        string[]? outFields,
        string objectIdFieldName,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();

        // Write attributes
        writer.WriteStartObject("attributes");

        if (feature.Attributes != null)
        {
            foreach (var kvp in feature.Attributes)
            {
                var fieldName = kvp.Key;

                // Skip fields not in outFields if specified
                if (outFields != null && !outFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                await WriteJsonValueAsync(writer, fieldName, kvp.Value, cancellationToken);
            }
        }

        writer.WriteEndObject(); // End attributes

        // Write geometry if requested and available
        if (returnGeometry && feature.Geometry != null)
        {
            writer.WritePropertyName("geometry");
            var geoServicesGeometry = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(
                feature.Geometry,
                null,
                geometryLimits,
                returnZ,
                returnM);
            JsonSerializer.Serialize(writer, geoServicesGeometry, FeatureServerJsonContext.Default.GeoServicesGeometry);
        }

        writer.WriteEndObject(); // End feature
    }

    /// <summary>
    /// Writes a single feature in GeoJSON format
    /// </summary>
    private async Task WriteGeoJsonFeatureAsync(
        Utf8JsonWriter writer,
        Feature feature,
        bool returnGeometry,
        string[]? outFields,
        string objectIdFieldName,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "Feature");

        // Write geometry if requested and available
        if (returnGeometry && feature.Geometry != null)
        {
            writer.WritePropertyName("geometry");
            var geoJsonGeometry = QueryFormatter.ConvertGeometryToGeoJsonFormat(
                feature.Geometry,
                geometryLimits,
                returnZ,
                returnM);
            if (geoJsonGeometry == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonSerializer.Serialize(writer, geoJsonGeometry, FeatureServerJsonContext.Default.GeoJsonGeometry);
            }
        }
        else
        {
            writer.WriteNull("geometry");
        }

        // Write properties
        writer.WriteStartObject("properties");

        if (feature.Attributes != null)
        {
            foreach (var kvp in feature.Attributes)
            {
                var fieldName = kvp.Key;

                // Skip fields not in outFields if specified
                if (outFields != null && !outFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                await WriteJsonValueAsync(writer, fieldName, kvp.Value, cancellationToken);
            }
        }

        writer.WriteEndObject(); // End properties
        writer.WriteEndObject(); // End feature
    }

    /// <summary>
    /// Writes a JSON value with proper type handling
    /// </summary>
    private static async Task WriteJsonValueAsync(
        Utf8JsonWriter writer,
        string propertyName,
        object? value,
        CancellationToken cancellationToken)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(propertyName);
                break;
            case string s:
                writer.WriteString(propertyName, s);
                break;
            case int i:
                writer.WriteNumber(propertyName, i);
                break;
            case long l:
                writer.WriteNumber(propertyName, l);
                break;
            case double d:
                writer.WriteNumber(propertyName, d);
                break;
            case float f:
                writer.WriteNumber(propertyName, f);
                break;
            case decimal dec:
                writer.WriteNumber(propertyName, dec);
                break;
            case bool b:
                writer.WriteBoolean(propertyName, b);
                break;
            case DateTime dt:
                writer.WriteString(propertyName, dt.ToString("O")); // ISO 8601 format
                break;
            case DateTimeOffset dto:
                writer.WriteString(propertyName, dto.ToString("O")); // ISO 8601 format
                break;
            default:
                // For complex objects, serialize to JSON and write as raw JSON
                writer.WritePropertyName(propertyName);
                JsonSerializer.Serialize(writer, value, FeatureServerJsonContext.Default.Object);
                break;
        }

        // Allow for cancellation during long attribute writing
        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Maps layer geometry type to GeoServices geometry type string
    /// </summary>
    private static string MapGeometryType(Honua.Core.Features.Catalog.Domain.GeometryType geometryType) => geometryType switch
    {
        Honua.Core.Features.Catalog.Domain.GeometryType.Point => "esriGeometryPoint",
        Honua.Core.Features.Catalog.Domain.GeometryType.LineString => "esriGeometryPolyline",
        Honua.Core.Features.Catalog.Domain.GeometryType.Polygon => "esriGeometryPolygon",
        Honua.Core.Features.Catalog.Domain.GeometryType.MultiPoint => "esriGeometryMultipoint",
        Honua.Core.Features.Catalog.Domain.GeometryType.MultiLineString => "esriGeometryPolyline",
        Honua.Core.Features.Catalog.Domain.GeometryType.MultiPolygon => "esriGeometryPolygon",
        Honua.Core.Features.Catalog.Domain.GeometryType.GeometryCollection => "esriGeometryNull",
        _ => "esriGeometryNull"
    };

}

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
    [ThreadStatic]
    private static WKBReader? _wkbReader;

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
        var queryFields = BuildQueryFields(layer, outFields, objectIdFieldName);
        var displayFieldName = ResolveDisplayFieldName(queryFields, objectIdFieldName);
        bool? hasZ = null;
        bool? hasM = null;
        if (layer.HasGeometry && returnGeometry)
        {
            hasZ = features.Any(feature => feature.Geometry?.HasZ == true);
            hasM = features.Any(feature => feature.Geometry?.HasM == true);
        }

        var srid = outputSrid ?? layer.SpatialReference.Wkid;
        var spatialReference = layer.HasGeometry
            ? new GeoServicesSpatialReference { Wkid = srid, LatestWkid = srid }
            : null;

        var response = new QueryResponse
        {
            ObjectIdFieldName = objectIdFieldName,
            GeometryType = layer.HasGeometry ? MapGeometryType(layer.GeometryType) : null,
            SpatialReference = spatialReference,
            DisplayFieldName = displayFieldName,
            Fields = queryFields,
            HasZ = hasZ,
            HasM = hasM,
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

        var exceededTransferLimit = result.HasMoreResults;
        var response = new GeoJsonFeatureSet
        {
            Features = features,
            ExceededTransferLimit = exceededTransferLimit,
            Properties = exceededTransferLimit
                ? new Dictionary<string, object?>
                {
                    ["exceededTransferLimit"] = true
                }
                : null
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
            IncludeGeometry = returnGeometry,
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

    internal static GeoServicesFieldInfo[] BuildQueryFields(
        LayerDefinition layer,
        string[]? outFields,
        string objectIdFieldName)
    {
        var includeAllFields = outFields == null || outFields.Length == 0
            || (outFields.Length == 1 && outFields[0].Equals("*", StringComparison.Ordinal));

        HashSet<string>? requestedFields = null;
        if (!includeAllFields)
        {
            requestedFields = new HashSet<string>(outFields!, StringComparer.OrdinalIgnoreCase)
            {
                objectIdFieldName
            };
        }

        var mappedFields = layer.Fields
            .Where(field => !field.IsGeometry)
            .Where(field => includeAllFields || requestedFields!.Contains(field.Name))
            .Select(MapFieldInfo)
            .ToList();

        if (!mappedFields.Any(field => field.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase)))
        {
            mappedFields.Add(new GeoServicesFieldInfo
            {
                Name = objectIdFieldName,
                Type = "esriFieldTypeOID",
                Alias = objectIdFieldName,
                Nullable = false,
                Editable = false
            });
        }

        return mappedFields.ToArray();
    }

    internal static string ResolveDisplayFieldName(IReadOnlyList<GeoServicesFieldInfo> fields, string objectIdFieldName)
    {
        if (fields.Count == 0)
        {
            return objectIdFieldName;
        }

        var preferredNameField = fields.FirstOrDefault(
            field => field.Name.Equals("name", StringComparison.OrdinalIgnoreCase));
        if (preferredNameField != null)
        {
            return preferredNameField.Name;
        }

        var firstStringField = fields.FirstOrDefault(
            field => field.Type.Equals("esriFieldTypeString", StringComparison.OrdinalIgnoreCase));
        return firstStringField?.Name ?? objectIdFieldName;
    }

    internal static GeoServicesFieldInfo MapFieldInfo(FieldDefinition field)
    {
        return new GeoServicesFieldInfo
        {
            Name = field.Name,
            Type = field.GeoServicesType,
            SqlType = field.SqlType,
            Alias = field.DisplayName,
            Length = field.Length,
            Nullable = field.Nullable,
            Editable = !field.IsGeometry,
            DefaultValue = field.DefaultValue
        };
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

        var reader = GetWkbReader();
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

    private static WKBReader GetWkbReader()
    {
        _wkbReader ??= new WKBReader();
        return _wkbReader;
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
        var rings = new List<double[][]>();

        if (polygon.ExteriorRing != null && !polygon.ExteriorRing.IsEmpty)
        {
            rings.Add(BuildLineStringCoordinates(polygon.ExteriorRing));
        }

        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            var interiorRing = polygon.GetInteriorRingN(i);
            if (interiorRing != null && !interiorRing.IsEmpty)
            {
                rings.Add(BuildLineStringCoordinates(interiorRing));
            }
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
    private const int FlushInterval = 32;
    private readonly GeometryLimits _geometryLimits;

    public StreamingQueryFormatter(IOptions<LimitsOptions> limitsOptions)
    {
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
        var outFieldLookup = CreateFieldLookup(outFields);
        var srid = outputSrid ?? layer.SpatialReference.Wkid;
        var queryFields = QueryFormatter.BuildQueryFields(layer, outFields, objectIdFieldName);
        var displayFieldName = QueryFormatter.ResolveDisplayFieldName(queryFields, objectIdFieldName);

        // Start object
        writer.WriteStartObject();

        // Write metadata
        writer.WriteString("objectIdFieldName", objectIdFieldName);
        writer.WriteString("displayFieldName", displayFieldName);
        writer.WritePropertyName("fields");
        JsonSerializer.Serialize(writer, queryFields, FeatureServerJsonContext.Default.GeoServicesFieldInfoArray);

        if (layer.HasGeometry)
        {
            writer.WriteString("geometryType", MapGeometryType(layer.GeometryType));
            writer.WriteStartObject("spatialReference");
            writer.WriteNumber("wkid", srid);
            writer.WriteNumber("latestWkid", srid);
            writer.WriteEndObject();

            if (returnGeometry)
            {
                // Streaming output cannot pre-scan all features; report requested dimensions.
                writer.WriteBoolean("hasZ", returnZ);
                writer.WriteBoolean("hasM", returnM);
            }
        }

        var effectiveLimits = GeometryOutputProcessor.CreateEffectiveLimits(
            _geometryLimits,
            geometryPrecision,
            maxAllowableOffset,
            forceSimplify: maxAllowableOffset is > 0);

        // Start features array
        writer.WriteStartArray("features");
        var featuresSinceFlush = 0;

        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            await WriteGeoServicesFeatureAsync(
                writer,
                feature,
                returnGeometry,
                outFieldLookup,
                objectIdFieldName,
                returnZ,
                returnM,
                effectiveLimits,
                cancellationToken);

            if (++featuresSinceFlush >= FlushInterval)
            {
                await writer.FlushAsync(cancellationToken);
                featuresSinceFlush = 0;
            }
        }

        // End features array
        writer.WriteEndArray();

        if (hasMoreResults)
        {
            // Write additional metadata only when exceeded (matches ArcGIS REST behavior)
            writer.WriteBoolean("exceededTransferLimit", true);
        }

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
        var outFieldLookup = CreateFieldLookup(outFields);

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
        var featuresSinceFlush = 0;

        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            await WriteGeoJsonFeatureAsync(
                writer,
                feature,
                returnGeometry,
                outFieldLookup,
                objectIdFieldName,
                returnZ,
                returnM,
                effectiveLimits,
                cancellationToken);

            if (++featuresSinceFlush >= FlushInterval)
            {
                await writer.FlushAsync(cancellationToken);
                featuresSinceFlush = 0;
            }
        }

        // End features array
        writer.WriteEndArray();

        if (hasMoreResults)
        {
            writer.WriteBoolean("exceededTransferLimit", true);
            writer.WriteStartObject("properties");
            writer.WriteBoolean("exceededTransferLimit", true);
            writer.WriteEndObject();
        }

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
        HashSet<string>? outFieldLookup,
        string objectIdFieldName,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();

        // Write attributes
        writer.WriteStartObject("attributes");

        // Ensure objectId is always present in attributes
        var objectIdWritten = false;
        if (feature.Attributes != null)
        {
            foreach (var kvp in feature.Attributes)
            {
                var fieldName = kvp.Key;

                // Skip fields not in outFields if specified
                if (outFieldLookup != null && !outFieldLookup.Contains(fieldName))
                {
                    continue;
                }

                if (string.Equals(fieldName, objectIdFieldName, StringComparison.OrdinalIgnoreCase))
                {
                    objectIdWritten = true;
                }

                await WriteJsonValueAsync(writer, fieldName, kvp.Value, cancellationToken);
            }
        }

        if (!objectIdWritten)
        {
            writer.WriteNumber(objectIdFieldName, feature.Id);
        }

        writer.WriteEndObject(); // End attributes

        // Write geometry if requested, even when null (matches GeoServices expectations)
        if (returnGeometry)
        {
            writer.WritePropertyName("geometry");
            if (feature.Geometry == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                var geoServicesGeometry = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(
                    feature.Geometry,
                    null,
                    geometryLimits,
                    returnZ,
                    returnM);
                JsonSerializer.Serialize(writer, geoServicesGeometry, FeatureServerJsonContext.Default.GeoServicesGeometry);
            }
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
        HashSet<string>? outFieldLookup,
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
                if (outFieldLookup != null && !outFieldLookup.Contains(fieldName))
                {
                    continue;
                }

                await WriteJsonValueAsync(writer, fieldName, kvp.Value, cancellationToken);
            }
        }

        writer.WriteEndObject(); // End properties
        writer.WriteEndObject(); // End feature
    }

    private static HashSet<string>? CreateFieldLookup(string[]? outFields)
    {
        if (outFields == null || outFields.Length == 0)
        {
            return null;
        }

        return new HashSet<string>(outFields, StringComparer.OrdinalIgnoreCase);
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
                writer.WriteNumber(propertyName, new DateTimeOffset(DateTime.SpecifyKind(
                    dt, dt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dt.Kind)).ToUnixTimeMilliseconds());
                break;
            case DateTimeOffset dto:
                writer.WriteNumber(propertyName, dto.ToUnixTimeMilliseconds());
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

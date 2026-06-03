// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.GeoJson;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NtsGeometryType = NetTopologySuite.IO.GeometryType;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Service for formatting query results into different output formats
/// </summary>
internal interface IQueryFormatter
{
    /// <summary>
    /// Formats query result into the specified format.
    /// </summary>
    ValueTask<(object response, string contentType)> FormatQueryResultAsync(
        QueryResult<Feature> result,
        MetadataV2Resource resource,
        string format,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        int? geometryPrecision,
        double? maxAllowableOffset,
        string[]? outFields = null,
        bool suppressObjectId = false);
}

/// <summary>
/// Implementation of query formatter service
/// </summary>
internal sealed class QueryFormatter : IQueryFormatter
{
    private readonly GeometryLimits _geometryLimits;
    private readonly PbfQueryFormatter _pbfFormatter;
    private readonly ILogger<QueryFormatter> _logger;

    public QueryFormatter(IOptions<LimitsOptions> limitsOptions, PbfQueryFormatter pbfFormatter, ILogger<QueryFormatter> logger)
    {
        _geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
        _pbfFormatter = pbfFormatter;
        _logger = logger;
    }

    public ValueTask<(object response, string contentType)> FormatQueryResultAsync(
        QueryResult<Feature> result,
        MetadataV2Resource resource,
        string format,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        int? geometryPrecision,
        double? maxAllowableOffset,
        string[]? outFields = null,
        bool suppressObjectId = false)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var effectiveLimits = GeometryOutputProcessor.CreateEffectiveLimits(
            _geometryLimits,
            geometryPrecision,
            maxAllowableOffset,
            forceSimplify: maxAllowableOffset is > 0);

        return format.ToLowerInvariant() switch
        {
            "pbf" => ValueTask.FromResult<(object response, string contentType)>(
                _pbfFormatter.FormatAsPbf(result, resource, returnGeometry, outputSrid, returnZ, returnM, geometryPrecision, maxAllowableOffset, outFields)),
            "geojson" => ValueTask.FromResult<(object response, string contentType)>(
                FormatAsGeoJson(result, resource, returnGeometry, returnZ, returnM, effectiveLimits, outFields)),
            "json" => ValueTask.FromResult<(object response, string contentType)>(
                FormatAsGeoServicesJson(result, resource, returnGeometry, outputSrid, returnZ, returnM, effectiveLimits, outFields, suppressObjectId)),
            "parquet" => ValueTask.FromResult<(object response, string contentType)>(
                FormatParquet(result, resource, returnGeometry, outputSrid, returnZ, returnM, effectiveLimits, outFields)),
            "arrow" => new ValueTask<(object response, string contentType)>(
                FormatArrowAsync(
                    result,
                    resource,
                    returnGeometry,
                    outputSrid,
                    returnZ,
                    returnM,
                    effectiveLimits,
                    outFields)),
            _ => ValueTask.FromResult<(object response, string contentType)>(
                FormatAsGeoServicesJson(result, resource, returnGeometry, outputSrid, returnZ, returnM, effectiveLimits, outFields, suppressObjectId))
        };
    }

    private (object response, string contentType) FormatParquet(
        QueryResult<Feature> result,
        MetadataV2Resource resource,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        GeometryLimits effectiveLimits,
        string[]? outFields)
    {
        var (response, contentType) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            result,
            resource,
            returnGeometry,
            outputSrid,
            returnZ,
            returnM,
            effectiveLimits,
            outFields,
            _logger);

        return (response, contentType);
    }

    private static async Task<(object response, string contentType)> FormatArrowAsync(
        QueryResult<Feature> result,
        MetadataV2Resource resource,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        GeometryLimits effectiveLimits,
        string[]? outFields)
    {
        var (response, contentType) = await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
            result,
            resource,
            returnGeometry,
            outputSrid,
            returnZ,
            returnM,
            effectiveLimits,
            outFields);

        return (response, contentType);
    }

    private static (object response, string contentType) FormatAsGeoServicesJson(
        QueryResult<Feature> result,
        MetadataV2Resource resource,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        string[]? outFields,
        bool suppressObjectId = false)
    {
        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        var allDeclaredAttributeFields = resource.SchemaFields
            .Where(static field => !IsGeometryField(field))
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visibleDeclaredAttributeFields = resource.SchemaFields
            .Where(static field => !field.Hidden && !IsGeometryField(field))
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runtimeFields = DetectRuntimeFields(result.Items, allDeclaredAttributeFields, objectIdFieldName);
        var runtimeFieldNames = runtimeFields
            .Select(field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        GeoServicesFeature[] features = result.Items
            .Select(f => ConvertToGeoServicesFeature(
                f,
                returnGeometry,
                outputSrid,
                outFields,
                visibleDeclaredAttributeFields,
                runtimeFieldNames,
                objectIdFieldName,
                returnZ,
                returnM,
                geometryLimits,
                suppressObjectId))
            .ToArray();
        var queryFields = BuildQueryFields(resource, outFields, objectIdFieldName, runtimeFields, suppressObjectId);
        var displayFieldName = ResolveDisplayFieldName(queryFields, objectIdFieldName);
        var geometryType = resource.ReadGeometryType();
        var hasGeometry = geometryType != MetadataV2GeometryType.None || resource.FindPrimaryGeometryField() is not null;
        var hasZ = false;
        var hasM = false;
        if (hasGeometry && returnGeometry)
        {
            hasZ = features.Any(feature => feature.Geometry?.HasZ == true);
            hasM = features.Any(feature => feature.Geometry?.HasM == true);
        }

        var srid = outputSrid ?? resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        var spatialReference = hasGeometry
            ? new GeoServicesSpatialReference { Wkid = srid, LatestWkid = srid }
            : null;

        var response = new QueryResponse
        {
            ObjectIdFieldName = objectIdFieldName,
            GeometryType = hasGeometry ? MapGeometryType(geometryType) : null,
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

    private static (object response, string contentType) FormatAsGeoJson(
        QueryResult<Feature> result,
        MetadataV2Resource resource,
        bool returnGeometry,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        string[]? outFields)
    {
        var projectedProperties = CreateProjectedProperties(outFields);
        var buildOptions = CreateFeatureServerGeoJsonBuildOptions(resource, projectedProperties);
        GeoJsonFeature[] features = result.Items
            .Select(f => ConvertToGeoJsonFeature(f, resource, returnGeometry, buildOptions, returnZ, returnM, geometryLimits))
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
    private static GeoServicesFeature ConvertToGeoServicesFeature(
        Feature feature,
        bool returnGeometry,
        int? outputSrid,
        string[]? outFields,
        IReadOnlySet<string> declaredAttributeFields,
        IReadOnlySet<string> runtimeAttributeFields,
        string objectIdFieldName,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        bool suppressObjectId = false)
    {
        Dictionary<string, object?> attributes = FilterAttributes(
            feature.Attributes,
            outFields,
            declaredAttributeFields,
            runtimeAttributeFields,
                objectIdFieldName,
                GeoServicesObjectIdFieldResolver.ResolveObjectIdValue(feature, objectIdFieldName),
                suppressObjectId);

        return new GeoServicesFeature
        {
            Attributes = attributes,
            IncludeGeometry = returnGeometry,
            Geometry = returnGeometry
                ? GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(
                    feature.Geometry,
                    srid: null,
                    geometryLimits,
                    returnZ,
                    returnM)
                : null
        };
    }

    private static GeoJsonFeature ConvertToGeoJsonFeature(
        Feature feature,
        MetadataV2Resource resource,
        bool returnGeometry,
        GeoJsonFeatureBuildOptions buildOptions,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits)
    {
        var featureBase = GeoJsonFeatureBaseBuilder.Create(feature, resource, buildOptions);
        var geometry = returnGeometry ? ConvertGeometryToGeoJsonFormat(feature.Geometry, geometryLimits, returnZ, returnM) : null;
        return featureBase.ToGeoJsonFeature(geometry);
    }

    /// <summary>
    /// Filters attributes based on outFields parameter
    /// </summary>
    private static Dictionary<string, object?> FilterAttributes(
        ImmutableDictionary<string, object?> attributes,
        string[]? outFields,
        IReadOnlySet<string> declaredAttributeFields,
        IReadOnlySet<string> runtimeAttributeFields,
        string objectIdFieldName,
        long objectIdValue,
        bool suppressObjectId = false)
    {
        if (outFields == null || outFields.Length == 0 ||
            (outFields.Length == 1 && outFields[0].Equals("*", StringComparison.Ordinal)))
        {
            var all = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in attributes)
            {
                if (ShouldIncludeGeoServicesAttribute(name, declaredAttributeFields, runtimeAttributeFields))
                {
                    all[name] = FeatureAttributeValueNormalizer.Normalize(value);
                }
            }

            // returnDistinctValues has no stable OID; do not force-append it (#1427).
            if (!suppressObjectId)
            {
                if (!all.ContainsKey(objectIdFieldName))
                {
                    all[objectIdFieldName] = objectIdValue;
                }
                AddObjectIdAlias(all, objectIdFieldName);
            }

            return all;
        }

        var filtered = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // Always include objectid field for GeoServices compatibility, except for
        // distinct results which intentionally carry only the requested outFields.
        if (!suppressObjectId)
        {
            if (attributes.TryGetValue(objectIdFieldName, out object? objectIdFromAttributes))
            {
                filtered[objectIdFieldName] = FeatureAttributeValueNormalizer.Normalize(objectIdFromAttributes);
            }
            else
            {
                filtered[objectIdFieldName] = objectIdValue;
            }
        }

        foreach (string field in outFields)
        {
            if (attributes.TryGetValue(field, out object? fieldValue)
                && ShouldIncludeGeoServicesAttribute(field, declaredAttributeFields, runtimeAttributeFields))
            {
                filtered[field] = FeatureAttributeValueNormalizer.Normalize(fieldValue);
            }
        }

        return filtered;
    }

    private static bool ShouldIncludeGeoServicesAttribute(
        string fieldName,
        IReadOnlySet<string> declaredAttributeFields,
        IReadOnlySet<string> runtimeAttributeFields)
    {
        if (IsInternalAttribute(fieldName))
        {
            return false;
        }

        return declaredAttributeFields.Contains(fieldName)
            || runtimeAttributeFields.Contains(fieldName);
    }

    private static bool IsInternalAttribute(string fieldName)
        => fieldName.StartsWith("__", StringComparison.Ordinal);

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

    internal static IReadOnlySet<string>? CreateProjectedProperties(string[]? outFields)
    {
        if (outFields == null || outFields.Length == 0)
        {
            return null;
        }

        return outFields.Length == 1 && outFields[0].Equals("*", StringComparison.Ordinal)
            ? null
            : outFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static GeoJsonFeatureBuildOptions CreateFeatureServerGeoJsonBuildOptions(
        MetadataV2Resource resource,
        IReadOnlySet<string>? projectedProperties)
        => new(
            ProjectedProperties: projectedProperties,
            IncludeObjectIdProperty: true,
            IncludeObjectIdAlias: projectedProperties is null &&
                                  GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource)
                                      .Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase),
            IncludeAdditionalAttributes: true,
            ResolveIdFromProperties: true);

    internal static GeoServicesFieldInfo[] BuildQueryFields(
        MetadataV2Resource resource,
        string[]? outFields,
        string objectIdFieldName,
        IReadOnlyCollection<GeoServicesFieldInfo>? runtimeFields = null,
        bool suppressObjectId = false)
    {
        var includeAllFields = outFields == null || outFields.Length == 0
            || (outFields.Length == 1 && outFields[0].Equals("*", StringComparison.Ordinal));

        HashSet<string>? requestedFields = null;
        if (!includeAllFields)
        {
            requestedFields = new HashSet<string>(outFields!, StringComparer.OrdinalIgnoreCase);
            if (!suppressObjectId)
            {
                requestedFields.Add(objectIdFieldName);
            }
        }

        var mappedFields = resource.SchemaFields
            .Where(static field => !IsGeometryField(field))
            .Where(static field => !field.Hidden)
            .Where(field => includeAllFields || requestedFields!.Contains(field.Name))
            .Select(field => MapFieldInfo(field, objectIdFieldName))
            .ToList();

        if (!suppressObjectId &&
            !mappedFields.Any(field => field.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase)))
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

        if (runtimeFields is { Count: > 0 })
        {
            foreach (var runtimeField in runtimeFields)
            {
                if (!includeAllFields && !requestedFields!.Contains(runtimeField.Name))
                {
                    continue;
                }

                if (mappedFields.Any(field => field.Name.Equals(runtimeField.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                mappedFields.Add(runtimeField);
            }
        }

        return mappedFields.ToArray();
    }

    internal static List<GeoServicesFieldInfo> DetectRuntimeFields(
        ImmutableArray<Feature> features,
        HashSet<string> declaredAttributeFields,
        string objectIdFieldName)
    {
        var runtimeFields = new List<GeoServicesFieldInfo>();
        var seenFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var feature in features)
        {
            foreach (var (name, value) in feature.Attributes)
            {
                if (seenFieldNames.Contains(name)
                    || declaredAttributeFields.Contains(name)
                    || name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase)
                    || IsInternalAttribute(name))
                {
                    continue;
                }

                var runtimeField = MapRuntimeFieldInfo(name, value);
                if (runtimeField is null)
                {
                    continue;
                }

                runtimeFields.Add(runtimeField);
                seenFieldNames.Add(name);
            }
        }

        return runtimeFields;
    }

    private static GeoServicesFieldInfo? MapRuntimeFieldInfo(string name, object? value)
    {
        return value switch
        {
            sbyte or byte or short or ushort or int or uint => CreateRuntimeFieldInfo(name, "esriFieldTypeInteger", "INTEGER"),
            long or ulong => CreateRuntimeFieldInfo(name, "esriFieldTypeInteger64", "BIGINT"),
            float => CreateRuntimeFieldInfo(name, "esriFieldTypeSingle", "REAL"),
            double or decimal => CreateRuntimeFieldInfo(name, "esriFieldTypeDouble", "DOUBLE PRECISION"),
            bool => CreateRuntimeFieldInfo(name, "esriFieldTypeSmallInteger", "BOOLEAN"),
            DateTimeOffset or DateTime => CreateRuntimeFieldInfo(name, "esriFieldTypeDate", "TIMESTAMP WITH TIME ZONE"),
            DateOnly => CreateRuntimeFieldInfo(name, "esriFieldTypeDate", "DATE"),
            TimeOnly or TimeSpan => CreateRuntimeFieldInfo(name, "esriFieldTypeString", "TIME"),
            Guid => CreateRuntimeFieldInfo(name, "esriFieldTypeGUID", "UUID"),
            byte[] => CreateRuntimeFieldInfo(name, "esriFieldTypeBlob", "BYTEA"),
            string text => CreateRuntimeFieldInfo(name, "esriFieldTypeString", "TEXT", text.Length),
            _ => null
        };
    }

    private static GeoServicesFieldInfo CreateRuntimeFieldInfo(
        string name,
        string type,
        string sqlType,
        int? length = null)
    {
        return new GeoServicesFieldInfo
        {
            Name = name,
            Type = type,
            SqlType = sqlType,
            Alias = name,
            Length = length,
            Nullable = true,
            Editable = false
        };
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

    internal static GeoServicesFieldInfo MapFieldInfo(MetadataV2Field field, string objectIdFieldName)
    {
        var isGeometry = IsGeometryField(field);
        // The layer's object-id field must be typed esriFieldTypeOID regardless of its
        // SQL type so Esri clients can locate the OID field by type (see issue #1299).
        var isObjectId = field.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase);
        return new GeoServicesFieldInfo
        {
            Name = field.Name,
            Type = isObjectId ? "esriFieldTypeOID" : MapFieldTypeToGeoServices(field.Type),
            SqlType = field.SqlType ?? MapFieldTypeToSql(field.Type),
            Alias = field.Alias ?? field.Title ?? field.Name,
            Length = field.Length,
            Nullable = field.Nullable && !isObjectId,
            Editable = field.Editable && !isGeometry && !isObjectId,
            DefaultValue = field.DefaultValue.HasValue ? ConvertJsonElement(field.DefaultValue.Value) : null,
            Domain = GeoServicesFieldDomainMapper.Map(field.Domain),
            Visible = !field.Hidden
        };
    }

    private static bool IsGeometryField(MetadataV2Field field)
        => field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography;

    private static string MapFieldTypeToGeoServices(MetadataV2FieldType type)
        => type switch
        {
            MetadataV2FieldType.String => "esriFieldTypeString",
            MetadataV2FieldType.Integer => "esriFieldTypeInteger",
            MetadataV2FieldType.BigInteger => "esriFieldTypeInteger64",
            MetadataV2FieldType.Double => "esriFieldTypeDouble",
            MetadataV2FieldType.Float => "esriFieldTypeSingle",
            MetadataV2FieldType.Boolean => "esriFieldTypeSmallInteger",
            MetadataV2FieldType.DateTime => "esriFieldTypeDate",
            MetadataV2FieldType.Date => "esriFieldTypeDate",
            MetadataV2FieldType.Time => "esriFieldTypeString",
            MetadataV2FieldType.Json => "esriFieldTypeString",
            MetadataV2FieldType.Binary => "esriFieldTypeBlob",
            MetadataV2FieldType.Uuid => "esriFieldTypeGUID",
            MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography => "esriFieldTypeGeometry",
            _ => "esriFieldTypeString"
        };

    private static string MapFieldTypeToSql(MetadataV2FieldType type)
        => type switch
        {
            MetadataV2FieldType.String => "TEXT",
            MetadataV2FieldType.Integer => "INTEGER",
            MetadataV2FieldType.BigInteger => "BIGINT",
            MetadataV2FieldType.Double => "DOUBLE PRECISION",
            MetadataV2FieldType.Float => "REAL",
            MetadataV2FieldType.Boolean => "BOOLEAN",
            MetadataV2FieldType.DateTime => "TIMESTAMP WITH TIME ZONE",
            MetadataV2FieldType.Date => "DATE",
            MetadataV2FieldType.Time => "TIME",
            MetadataV2FieldType.Json => "JSONB",
            MetadataV2FieldType.Binary => "BYTEA",
            MetadataV2FieldType.Uuid => "UUID",
            MetadataV2FieldType.Geometry => "GEOMETRY",
            MetadataV2FieldType.Geography => "GEOGRAPHY",
            _ => "TEXT"
        };

    private static object? ConvertJsonElement(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue :
                                    element.TryGetDouble(out var doubleValue) ? doubleValue :
                                    element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.Clone()
        };

    /// <summary>
    /// Converts WKB geometry to GeoJSON format
    /// </summary>
    internal static GeoJsonGeometry? ConvertGeometryToGeoJsonFormat(
        byte[]? wkbGeometry,
        GeometryLimits geometryLimits,
        bool returnZ,
        bool returnM)
    {
        // GeoJSON/RFC 7946 positions may include altitude, but not M as a fourth ordinate.
        returnM = false;

        if (wkbGeometry == null || wkbGeometry.Length == 0)
            return null;

        var reader = WkbReaderCache.Get();
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

    private static string MapGeometryType(MetadataV2GeometryType geometryType)
        => geometryType switch
        {
            MetadataV2GeometryType.Point => "esriGeometryPoint",
            MetadataV2GeometryType.LineString => "esriGeometryPolyline",
            MetadataV2GeometryType.Polygon => "esriGeometryPolygon",
            MetadataV2GeometryType.MultiPoint => "esriGeometryMultipoint",
            MetadataV2GeometryType.MultiLineString => "esriGeometryPolyline",
            MetadataV2GeometryType.MultiPolygon => "esriGeometryPolygon",
            MetadataV2GeometryType.GeometryCollection or MetadataV2GeometryType.Mixed or MetadataV2GeometryType.None => "esriGeometryNull",
            _ => "esriGeometryNull"
        };

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

    public async Task StreamAsGeoServicesJsonAsync(
        IAsyncEnumerable<Feature> features,
        MetadataV2Resource resource,
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

        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        var outFieldLookup = CreateFieldLookup(outFields);
        var srid = outputSrid ?? resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        var queryFields = QueryFormatter.BuildQueryFields(resource, outFields, objectIdFieldName);
        var allDeclaredAttributeFields = resource.SchemaFields
            .Where(static field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visibleDeclaredAttributeFields = resource.SchemaFields
            .Where(static field => !field.Hidden &&
                                   field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var displayFieldName = QueryFormatter.ResolveDisplayFieldName(queryFields, objectIdFieldName);
        var geometryType = resource.ReadGeometryType();
        var hasGeometry = geometryType != MetadataV2GeometryType.None || resource.FindPrimaryGeometryField() is not null;

        writer.WriteStartObject();

        writer.WriteString("objectIdFieldName", objectIdFieldName);
        writer.WriteString("displayFieldName", displayFieldName);
        writer.WritePropertyName("fields");
        JsonSerializer.Serialize(writer, queryFields, FeatureServerJsonContext.Default.GeoServicesFieldInfoArray);

        if (hasGeometry)
        {
            writer.WriteString("geometryType", MapGeometryType(geometryType));
            writer.WriteStartObject("spatialReference");
            writer.WriteNumber("wkid", srid);
            writer.WriteNumber("latestWkid", srid);
            writer.WriteEndObject();

            if (returnGeometry)
            {
                writer.WriteBoolean("hasZ", returnZ);
                writer.WriteBoolean("hasM", returnM);
            }
        }

        var effectiveLimits = GeometryOutputProcessor.CreateEffectiveLimits(
            _geometryLimits,
            geometryPrecision,
            maxAllowableOffset,
            forceSimplify: maxAllowableOffset is > 0);

        writer.WriteStartArray("features");
        var featuresSinceFlush = 0;

        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            WriteGeoServicesFeature(
                writer,
                feature,
                returnGeometry,
                outputSrid,
                outFieldLookup,
                allDeclaredAttributeFields,
                visibleDeclaredAttributeFields,
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

        writer.WriteEndArray();

        // Always emit exceededTransferLimit (including false). Esri's GeoServices query
        // contract always returns this field; omitting the false case made the ArcGIS API
        // for Python paginator dereference a missing value (fetched >= None -> TypeError).
        writer.WriteBoolean("exceededTransferLimit", hasMoreResults);

        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken);
    }

    public async Task StreamAsGeoJsonAsync(
        IAsyncEnumerable<Feature> features,
        MetadataV2Resource resource,
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

        var projectedProperties = QueryFormatter.CreateProjectedProperties(outFields);
        var buildOptions = QueryFormatter.CreateFeatureServerGeoJsonBuildOptions(resource, projectedProperties);

        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");

        writer.WriteStartArray("features");

        var effectiveLimits = GeometryOutputProcessor.CreateEffectiveLimits(
            _geometryLimits,
            geometryPrecision,
            maxAllowableOffset,
            forceSimplify: maxAllowableOffset is > 0);
        var featuresSinceFlush = 0;

        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            WriteGeoJsonFeature(
                writer,
                feature,
                resource,
                returnGeometry,
                buildOptions,
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

        writer.WriteEndArray();

        if (hasMoreResults)
        {
            writer.WriteBoolean("exceededTransferLimit", true);
            writer.WriteStartObject("properties");
            writer.WriteBoolean("exceededTransferLimit", true);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes a single feature in GeoServices format
    /// </summary>
    private static void WriteGeoServicesFeature(
        Utf8JsonWriter writer,
        Feature feature,
        bool returnGeometry,
        int? outputSrid,
        HashSet<string>? outFieldLookup,
        IReadOnlySet<string> allDeclaredAttributeFields,
        IReadOnlySet<string> visibleDeclaredAttributeFields,
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

                if (!ShouldWriteStreamingAttribute(fieldName, allDeclaredAttributeFields, visibleDeclaredAttributeFields))
                {
                    continue;
                }

                if (string.Equals(fieldName, objectIdFieldName, StringComparison.OrdinalIgnoreCase))
                {
                    objectIdWritten = true;
                }

                WriteJsonValue(writer, fieldName, kvp.Value, cancellationToken);
            }
        }

        if (!objectIdWritten)
        {
            writer.WriteNumber(objectIdFieldName, GeoServicesObjectIdFieldResolver.ResolveObjectIdValue(feature, objectIdFieldName));
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
            else if (!GeoServicesGeometryConverter.TryWriteWkbAsGeoServicesGeometry(
                         writer,
                         feature.Geometry,
                         srid: null,
                         geometryLimits,
                         returnZ,
                         returnM))
            {
                var geoServicesGeometry = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(
                    feature.Geometry,
                    srid: null,
                    geometryLimits,
                    returnZ,
                    returnM);
                JsonSerializer.Serialize(writer, geoServicesGeometry, FeatureServerJsonContext.Default.GeoServicesGeometry);
            }
        }

        writer.WriteEndObject(); // End feature
    }

    private static bool ShouldWriteStreamingAttribute(
        string fieldName,
        IReadOnlySet<string> allDeclaredAttributeFields,
        IReadOnlySet<string> visibleDeclaredAttributeFields)
    {
        if (fieldName.StartsWith("__", StringComparison.Ordinal))
        {
            return false;
        }

        if (visibleDeclaredAttributeFields.Contains(fieldName))
        {
            return true;
        }

        return !allDeclaredAttributeFields.Contains(fieldName);
    }

    private static void WriteGeoJsonFeature(
        Utf8JsonWriter writer,
        Feature feature,
        MetadataV2Resource resource,
        bool returnGeometry,
        GeoJsonFeatureBuildOptions buildOptions,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        CancellationToken cancellationToken)
    {
        var featureBase = GeoJsonFeatureBaseBuilder.Create(feature, resource, buildOptions);

        writer.WriteStartObject();
        writer.WriteString("type", "Feature");
        writer.WritePropertyName("id");
        JsonSerializer.Serialize(writer, featureBase.Id, FeatureServerJsonContext.Default.Object);

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

        writer.WriteStartObject("properties");
        foreach (var kvp in featureBase.Properties)
        {
            WriteJsonValue(writer, kvp.Key, kvp.Value, cancellationToken);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
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
    private static void WriteJsonValue(
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
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string MapGeometryType(MetadataV2GeometryType geometryType)
        => geometryType switch
        {
            MetadataV2GeometryType.Point => "esriGeometryPoint",
            MetadataV2GeometryType.LineString => "esriGeometryPolyline",
            MetadataV2GeometryType.Polygon => "esriGeometryPolygon",
            MetadataV2GeometryType.MultiPoint => "esriGeometryMultipoint",
            MetadataV2GeometryType.MultiLineString => "esriGeometryPolyline",
            MetadataV2GeometryType.MultiPolygon => "esriGeometryPolygon",
            MetadataV2GeometryType.GeometryCollection or MetadataV2GeometryType.Mixed or MetadataV2GeometryType.None => "esriGeometryNull",
            _ => "esriGeometryNull"
        };

}

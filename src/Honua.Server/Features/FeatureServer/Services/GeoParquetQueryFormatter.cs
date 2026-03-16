// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using ParquetSharp.Arrow;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Service for formatting query results as GeoParquet
/// </summary>
internal sealed class GeoParquetQueryFormatter
{
    // Constants for GeoParquet format
    private const string GeometryColumnName = "geometry";
    private const string GeoParquetVersion = "1.1.0";
    private const string GeometryEncoding = "WKB";
    private const string GeoMetadataKey = "geo";
    private const string ContentType = "application/vnd.apache.parquet";

    [ThreadStatic]
    private static WKBReader? _wkbReader;


    /// <summary>
    /// Formats query result as GeoParquet
    /// </summary>
    /// <param name="result">Query result with features</param>
    /// <param name="layer">Layer definition for metadata</param>
    /// <param name="returnGeometry">Whether to include geometry</param>
    /// <param name="outputSrid">Output SRID for geometry</param>
    /// <param name="returnZ">Whether to include Z values</param>
    /// <param name="returnM">Accepted for API symmetry but M values are always stripped.
    /// GeoParquet 1.1.0 only supports XY and XYZ geometries.</param>
    /// <param name="geometryLimits">Pre-computed effective geometry limits (precision, simplification).</param>
    /// <param name="outFields">Fields to include in output</param>
    /// <param name="logger">Optional logger for conversion diagnostics</param>
    /// <returns>Formatted result as byte array and content type</returns>
    public static (byte[] response, string contentType) FormatAsGeoParquet(
        QueryResult<Feature> result,
        LayerDefinition layer,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        string[]? outFields = null,
        ILogger? logger = null)
    {
        var features = result.Items;

        if (features.Length == 0)
        {
            // Return empty GeoParquet file with schema only
            return CreateEmptyGeoParquet(layer, returnGeometry, outFields, outputSrid, returnZ);
        }

        // Detect runtime-computed attributes (e.g. "distance" from KNN queries) that
        // exist in the result set but are not declared in the layer schema.
        var runtimeFields = DetectRuntimeFields(features, layer);

        // Build geometry column first so we know whether any geometry actually has Z.
        // This drives the GeoParquet metadata `geometry_types` truthfully rather than
        // relying on the `returnZ` flag which may not match the actual data.
        BinaryArray? geometryArray = null;
        bool anyGeometryHasZ = false;
        if (returnGeometry && layer.HasGeometry)
        {
            (geometryArray, anyGeometryHasZ) = BuildGeometryArray(
                features,
                outputSrid ?? layer.SpatialReference.Wkid,
                returnZ,
                returnM,
                geometryLimits);
        }

        var (schema, fieldsToInclude, objectIdFieldName) = BuildSchema(
            layer, returnGeometry, outFields, outputSrid,
            advertiseZ: anyGeometryHasZ,
            runtimeFields);

        // Build record batch
        var arrays = BuildArrays(
            features,
            schema,
            layer,
            returnGeometry,
            geometryArray,
            objectIdFieldName,
            fieldsToInclude,
            logger);

        using var recordBatch = new RecordBatch(schema, arrays, features.Length);

        // Write to Parquet format
        using var stream = new MemoryStream();
        var arrowWriterProperties = new ArrowWriterPropertiesBuilder().StoreSchema().Build();
        using (var writer = new FileWriter(stream, schema, null, arrowWriterProperties, true))
        {
            writer.WriteRecordBatch(recordBatch);
            writer.Close();
        }

        return (stream.ToArray(), ContentType);
    }

    /// <summary>
    /// Creates empty GeoParquet file with schema only
    /// </summary>
    private static (byte[] response, string contentType) CreateEmptyGeoParquet(
        LayerDefinition layer,
        bool returnGeometry,
        string[]? outFields,
        int? outputSrid,
        bool returnZ)
    {
        var (schema, _, _) = BuildSchema(layer, returnGeometry, outFields, outputSrid, returnZ);

        using var stream = new MemoryStream();
        var arrowWriterProperties = new ArrowWriterPropertiesBuilder().StoreSchema().Build();
        using (var writer = new FileWriter(stream, schema, null, arrowWriterProperties, true))
        {
            writer.Close();
        }

        return (stream.ToArray(), ContentType);
    }

    /// <summary>
    /// Builds the Arrow schema and resolves which attribute fields to include.
    /// Shared by both populated and empty GeoParquet paths.
    /// </summary>
    /// <param name="layer">Layer definition for schema metadata.</param>
    /// <param name="returnGeometry">Whether to include the geometry column.</param>
    /// <param name="outFields">Requested output fields, or null / ["*"] for all.</param>
    /// <param name="outputSrid">Output SRID for CRS metadata.</param>
    /// <param name="advertiseZ">Whether Z dimensions should be advertised in GeoParquet metadata.
    /// For populated results this reflects actual geometry content; for empty results it mirrors returnZ.</param>
    /// <param name="runtimeFields">
    /// Runtime-computed attributes detected from the result set (e.g. "distance" from KNN queries).
    /// These exist in <c>feature.Attributes</c> but are not part of the layer schema.
    /// Pass an empty list for the empty-result path.
    /// </param>
    private static (Schema schema, List<FieldDefinition> fieldsToInclude, string objectIdFieldName) BuildSchema(
        LayerDefinition layer,
        bool returnGeometry,
        string[]? outFields,
        int? outputSrid,
        bool advertiseZ,
        IReadOnlyList<(string name, IArrowType type)>? runtimeFields = null)
    {
        var objectIdFieldName = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;
        var includeAllFields = outFields == null || outFields.Length == 0 ||
                              (outFields.Length == 1 && outFields[0].Equals("*", StringComparison.Ordinal));

        var schemaFields = new List<Field>
        {
            new Field(objectIdFieldName, new Int64Type(), false)
        };

        if (returnGeometry && layer.HasGeometry)
        {
            schemaFields.Add(new Field(GeometryColumnName, new BinaryType(), true));
        }

        var fieldsToInclude = (includeAllFields
            ? layer.Fields.Where(f => !f.IsGeometry && !f.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase))
            : layer.Fields.Where(f => !f.IsGeometry && !f.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase) && outFields!.Contains(f.Name, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        foreach (var field in fieldsToInclude)
        {
            schemaFields.Add(new Field(field.Name, MapToArrowType(field), field.Nullable));
        }

        // Append runtime-computed attributes (e.g. "distance" from KNN queries) that are
        // not declared in the layer schema but appear in feature.Attributes.
        // Only include when outFields is omitted / "*", or the field was explicitly requested,
        // to match the filtering behavior of JSON/GeoJSON/PBF formatters.
        if (runtimeFields != null)
        {
            foreach (var (name, type) in runtimeFields)
            {
                if (includeAllFields || outFields!.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    schemaFields.Add(new Field(name, type, nullable: true));
                }
            }
        }

        var schema = new Schema(schemaFields, BuildGeoParquetMetadata(layer, returnGeometry, outputSrid, advertiseZ));
        return (schema, fieldsToInclude, objectIdFieldName);
    }

    /// <summary>
    /// Builds GeoParquet metadata following the specification
    /// </summary>
    private static Dictionary<string, string> BuildGeoParquetMetadata(
        LayerDefinition layer,
        bool returnGeometry,
        int? outputSrid,
        bool advertiseZ)
    {
        var metadata = new Dictionary<string, string>();

        if (!returnGeometry || !layer.HasGeometry)
        {
            return metadata;
        }

        var srid = outputSrid ?? layer.SpatialReference.Wkid;

        // Build JSON manually to avoid AOT issues.
        // GeoParquet 1.1.0 spec CRS rules:
        //   - Omitting the `crs` key implies OGC:CRS84 (WGS84 lon/lat).
        //   - `"crs": null` means the CRS is undefined/unknown.
        //   - Any present `crs` value must be a full PROJJSON CRS object.
        // For EPSG:4326 we omit the key (spec-compliant OGC:CRS84 default).
        // For other SRIDs we write null because generating valid PROJJSON requires
        // a CRS lookup table or projection library; tracked as follow-up.
        var crsPart = srid == 4326
            ? ""
            : ",\"crs\":null";

        // bbox is omitted: GeoParquet 1.1.0 defines bbox as the bounding box of the
        // geometries *in the file*, but we only have the full layer extent which would
        // be incorrect for filtered or empty exports. Computing the actual result bbox
        // would require parsing every WKB geometry; deferred to a follow-up.

        var geomType = MapGeometryTypeToGeoParquet(layer.GeometryType, advertiseZ);
        var geoJson = $@"{{""version"":""{GeoParquetVersion}"",""primary_column"":""{GeometryColumnName}"",""columns"":{{""{GeometryColumnName}"":{{""encoding"":""{GeometryEncoding}"",""geometry_types"":[""{geomType}""]{crsPart}}}}}}}";

        metadata[GeoMetadataKey] = geoJson;

        return metadata;
    }


    /// <summary>
    /// Detects runtime-computed attributes present in the result set but not declared in the
    /// layer schema (e.g. the "distance" field injected by KNN queries when returnDistance=true).
    /// Internal fields prefixed with "__" are excluded.
    /// </summary>
    private static List<(string name, IArrowType type)> DetectRuntimeFields(
        ImmutableArray<Feature> features,
        LayerDefinition layer)
    {
        var result = new List<(string name, IArrowType type)>();
        if (features.Length == 0) return result;

        var layerFieldNames = new HashSet<string>(
            layer.Fields.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var seenRuntimeFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var feature in features)
        {
            foreach (var (key, value) in feature.Attributes)
            {
                if (seenRuntimeFields.Contains(key) || layerFieldNames.Contains(key)) continue;
                // Skip internal fields (e.g. __honua_total_count)
                if (key.StartsWith("__", StringComparison.Ordinal)) continue;

                var arrowType = InferArrowTypeFromValue(value);
                if (arrowType == null) continue;

                result.Add((key, arrowType));
                seenRuntimeFields.Add(key);
            }
        }

        return result;
    }

    /// <summary>
    /// Infers an Arrow type from a CLR runtime value. Returns null for unrecognised types.
    /// </summary>
    private static IArrowType? InferArrowTypeFromValue(object? value)
    {
        return value switch
        {
            double => new DoubleType(),
            float => new FloatType(),
            int => new Int32Type(),
            long => new Int64Type(),
            bool => new BooleanType(),
            string => new StringType(),
            DateOnly => new Date32Type(),
            TimeOnly => new Time32Type(TimeUnit.Millisecond),
            TimeSpan => new Time32Type(TimeUnit.Millisecond),
            DateTime => new TimestampType(TimeUnit.Millisecond, TimeZoneInfo.Utc),
            DateTimeOffset => new TimestampType(TimeUnit.Millisecond, TimeZoneInfo.Utc),
            _ => null
        };
    }

    /// <summary>
    /// Maps layer geometry type to GeoParquet geometry type string
    /// </summary>
    private static string MapGeometryTypeToGeoParquet(Core.Features.Catalog.Domain.GeometryType geometryType, bool returnZ)
    {
        var baseType = geometryType switch
        {
            Core.Features.Catalog.Domain.GeometryType.Point => "Point",
            Core.Features.Catalog.Domain.GeometryType.LineString => "LineString",
            Core.Features.Catalog.Domain.GeometryType.Polygon => "Polygon",
            Core.Features.Catalog.Domain.GeometryType.MultiPoint => "MultiPoint",
            Core.Features.Catalog.Domain.GeometryType.MultiLineString => "MultiLineString",
            Core.Features.Catalog.Domain.GeometryType.MultiPolygon => "MultiPolygon",
            Core.Features.Catalog.Domain.GeometryType.GeometryCollection => "GeometryCollection",
            _ => "Geometry"
        };

        return returnZ ? $"{baseType} Z" : baseType;
    }

    /// <summary>
    /// Maps field definition to Arrow data type.
    /// IMPORTANT: keep in sync with <see cref="BuildAttributeArray"/> which selects the
    /// matching typed builder for the same SQL type strings.
    /// </summary>
    private static IArrowType MapToArrowType(FieldDefinition field)
    {
        return field.SqlType.ToLowerInvariant() switch
        {
            "bigint" or "int8" => new Int64Type(),
            "integer" or "int4" => new Int32Type(),
            "smallint" or "int2" => new Int16Type(),
            "real" or "float4" => new FloatType(),
            "double precision" or "float8" => new DoubleType(),
            "boolean" or "bool" => new BooleanType(),
            "date" => new Date32Type(),
            "time" => new Time32Type(TimeUnit.Millisecond),
            "timestamp" or "timestamptz" or "timestamp with time zone" or "timestamp without time zone" => new TimestampType(TimeUnit.Millisecond, TimeZoneInfo.Utc),
            "bytea" => new BinaryType(),
            "uuid" => new StringType(),
            "json" or "jsonb" => new StringType(),
            _ when field.SqlType.StartsWith("varchar", StringComparison.OrdinalIgnoreCase) => new StringType(),
            _ when field.SqlType.StartsWith("char", StringComparison.OrdinalIgnoreCase) => new StringType(),
            _ when field.SqlType.StartsWith("text", StringComparison.OrdinalIgnoreCase) => new StringType(),
            _ when field.SqlType.StartsWith("numeric", StringComparison.OrdinalIgnoreCase) => new DoubleType(),
            _ when field.SqlType.StartsWith("decimal", StringComparison.OrdinalIgnoreCase) => new DoubleType(),
            _ => new StringType() // Default to string for unknown types
        };
    }

    /// <summary>
    /// Builds Arrow arrays for each column.
    /// The geometry column is pre-built and passed in (null when geometry is excluded).
    /// </summary>
    private static IArrowArray[] BuildArrays(
        ImmutableArray<Feature> features,
        Schema schema,
        LayerDefinition layer,
        bool returnGeometry,
        BinaryArray? geometryArray,
        string objectIdFieldName,
        List<FieldDefinition> fieldsToInclude,
        ILogger? logger = null)
    {
        var arrays = new List<IArrowArray>();

        foreach (var field in schema.FieldsList)
        {
            if (field.Name == objectIdFieldName)
            {
                arrays.Add(BuildObjectIdArray(features));
            }
            else if (field.Name == GeometryColumnName && returnGeometry && geometryArray != null)
            {
                arrays.Add(geometryArray);
            }
            else
            {
                var fieldDef = fieldsToInclude.FirstOrDefault(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
                if (fieldDef != null)
                {
                    arrays.Add(BuildAttributeArray(features, field.Name, fieldDef, logger));
                }
                else
                {
                    // Runtime-computed attribute (e.g. "distance" from KNN queries):
                    // no FieldDefinition exists, so dispatch by the Arrow type declared in the schema.
                    arrays.Add(BuildRuntimeAttributeArray(features, field, logger));
                }
            }
        }

        return arrays.ToArray();
    }

    /// <summary>
    /// Builds Int64 array for object IDs directly from features without intermediate allocation
    /// </summary>
    private static Int64Array BuildObjectIdArray(ImmutableArray<Feature> features)
    {
        var builder = new Int64Array.Builder();
        foreach (var feature in features)
        {
            builder.Append(feature.Id);
        }
        return builder.Build();
    }

    /// <summary>
    /// Builds binary array for WKB geometry data and tracks whether any geometry retains Z coordinates.
    /// </summary>
    private static (BinaryArray array, bool anyHasZ) BuildGeometryArray(
        ImmutableArray<Feature> features,
        int outputSrid,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits)
    {
        var builder = new BinaryArray.Builder();
        var anyHasZ = false;

        foreach (var feature in features)
        {
            var (wkb, hasZ) = ProcessGeometry(feature.Geometry, outputSrid, geometryLimits, returnZ, returnM);
            anyHasZ |= hasZ;
            if (wkb != null && wkb.Length > 0)
            {
                builder.Append(wkb);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return (builder.Build(), anyHasZ);
    }

    private static (byte[]? wkb, bool hasZ) ProcessGeometry(
        byte[]? geometryBytes,
        int outputSrid,
        GeometryLimits geometryLimits,
        bool returnZ,
        bool returnM)
    {
        if (geometryBytes == null || geometryBytes.Length == 0)
        {
            return (null, false);
        }

        Geometry? geometry;
        try
        {
            geometry = GetWkbReader().Read(geometryBytes);
        }
        catch (Exception ex) when (ex is ParseException or FormatException)
        {
            return (null, false);
        }

        if (geometry == null)
        {
            return (null, false);
        }

        geometry.SRID = outputSrid;
        geometry = GeometryOutputProcessor.ApplyLimits(geometry, geometryLimits);
        if (geometry == null)
        {
            return (null, false);
        }

        // GeoParquet 1.1.0 only supports XY and XYZ — always strip M values.
        geometry = GeometryOutputProcessor.ApplyDimensionFilter(geometry, returnZ, includeM: false);

        var hasZ = GeometryHasZ(geometry);
        var writer = new WKBWriter(
            ByteOrder.LittleEndian,
            handleSRID: false,
            emitZ: hasZ,
            emitM: false);
        return (writer.Write(geometry), hasZ);
    }

    /// <summary>
    /// Builds attribute array for a specific field.
    /// IMPORTANT: keep in sync with <see cref="MapToArrowType"/> which declares the
    /// Arrow schema type for the same SQL type strings.
    /// </summary>
    private static IArrowArray BuildAttributeArray(
        ImmutableArray<Feature> features,
        string fieldName,
        FieldDefinition fieldDef,
        ILogger? logger = null)
    {
        var sqlType = fieldDef.SqlType.ToLowerInvariant();
        return sqlType switch
        {
            "bigint" or "int8" => BuildInt64AttributeArray(features, fieldName, logger),
            "integer" or "int4" => BuildInt32AttributeArray(features, fieldName, logger),
            "smallint" or "int2" => BuildInt16AttributeArray(features, fieldName, logger),
            "real" or "float4" => BuildFloatAttributeArray(features, fieldName, logger),
            "double precision" or "float8" => BuildDoubleAttributeArray(features, fieldName, logger),
            "boolean" or "bool" => BuildBooleanAttributeArray(features, fieldName, logger),
            "bytea" => BuildBinaryAttributeArray(features, fieldName, logger),
            "date" => BuildDate32AttributeArray(features, fieldName, logger),
            "time" => BuildTime32AttributeArray(features, fieldName, logger),
            "timestamp" or "timestamptz" or "timestamp with time zone" or "timestamp without time zone" => BuildTimestampAttributeArray(features, fieldName, logger),
            _ when sqlType.StartsWith("numeric", StringComparison.OrdinalIgnoreCase) => BuildDoubleAttributeArray(features, fieldName, logger),
            _ when sqlType.StartsWith("decimal", StringComparison.OrdinalIgnoreCase) => BuildDoubleAttributeArray(features, fieldName, logger),
            _ => BuildStringAttributeArray(features, fieldName, logger)
        };
    }


    /// <summary>
    /// Iterates features extracting attribute values, handling nulls and conversion errors.
    /// </summary>
    private static void ForEachAttribute(
        ImmutableArray<Feature> features,
        string fieldName,
        Action<object> onValue,
        Action onNull,
        ILogger? logger = null)
    {
        foreach (var feature in features)
        {
            if (feature.Attributes.TryGetValue(fieldName, out var value) && value != null)
            {
                try
                {
                    onValue(value);
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    if (logger != null)
                    {
                        FeatureServerLog.GeoParquetConversionFailed(logger, fieldName, value.GetType().Name, ex);
                    }

                    onNull();
                }
            }
            else
            {
                onNull();
            }
        }
    }

    private static Int64Array BuildInt64AttributeArray(ImmutableArray<Feature> features, string fieldName, ILogger? logger = null)
    {
        var builder = new Int64Array.Builder();
        ForEachAttribute(features, fieldName, v => builder.Append(Convert.ToInt64(v)), () => builder.AppendNull(), logger);
        return builder.Build();
    }

    private static Int32Array BuildInt32AttributeArray(ImmutableArray<Feature> features, string fieldName, ILogger? logger = null)
    {
        var builder = new Int32Array.Builder();
        ForEachAttribute(features, fieldName, v => builder.Append(Convert.ToInt32(v)), () => builder.AppendNull(), logger);
        return builder.Build();
    }

    private static Int16Array BuildInt16AttributeArray(ImmutableArray<Feature> features, string fieldName, ILogger? logger = null)
    {
        var builder = new Int16Array.Builder();
        ForEachAttribute(features, fieldName, v => builder.Append(Convert.ToInt16(v)), () => builder.AppendNull(), logger);
        return builder.Build();
    }

    private static FloatArray BuildFloatAttributeArray(ImmutableArray<Feature> features, string fieldName, ILogger? logger = null)
    {
        var builder = new FloatArray.Builder();
        ForEachAttribute(features, fieldName, v => builder.Append(Convert.ToSingle(v)), () => builder.AppendNull(), logger);
        return builder.Build();
    }

    private static DoubleArray BuildDoubleAttributeArray(ImmutableArray<Feature> features, string fieldName, ILogger? logger = null)
    {
        var builder = new DoubleArray.Builder();
        ForEachAttribute(features, fieldName, v => builder.Append(Convert.ToDouble(v)), () => builder.AppendNull(), logger);
        return builder.Build();
    }

    private static BooleanArray BuildBooleanAttributeArray(ImmutableArray<Feature> features, string fieldName, ILogger? logger = null)
    {
        var builder = new BooleanArray.Builder();
        ForEachAttribute(features, fieldName, v => builder.Append(Convert.ToBoolean(v)), () => builder.AppendNull(), logger);
        return builder.Build();
    }

    private static Date32Array BuildDate32AttributeArray(ImmutableArray<Feature> features, string fieldName, ILogger? logger = null)
    {
        var builder = new Date32Array.Builder();
        ForEachAttribute(
            features,
            fieldName,
            v =>
            {
                var dateTime = TryConvertDateTimeValue(v);
                if (dateTime.HasValue)
                {
                    builder.Append(dateTime.Value);
                }
                else
                {
                    builder.AppendNull();
                }
            },
            () => builder.AppendNull(),
            logger);
        return builder.Build();
    }

    private static Time32Array BuildTime32AttributeArray(ImmutableArray<Feature> features, string fieldName, ILogger? logger = null)
    {
        var builder = new Time32Array.Builder(TimeUnit.Millisecond);
        ForEachAttribute(
            features,
            fieldName,
            v =>
            {
                var milliseconds = TryConvertTimeMilliseconds(v);
                if (milliseconds.HasValue)
                {
                    builder.Append(milliseconds.Value);
                }
                else
                {
                    builder.AppendNull();
                }
            },
            () => builder.AppendNull(),
            logger);
        return builder.Build();
    }

    private static TimestampArray BuildTimestampAttributeArray(ImmutableArray<Feature> features, string fieldName, ILogger? logger = null)
    {
        var builder = new TimestampArray.Builder(TimeUnit.Millisecond, TimeZoneInfo.Utc);
        ForEachAttribute(
            features,
            fieldName,
            v =>
            {
                var dateTime = TryConvertDateTimeValue(v);
                if (dateTime.HasValue)
                {
                    builder.Append(new DateTimeOffset(dateTime.Value));
                }
                else
                {
                    builder.AppendNull();
                }
            },
            () => builder.AppendNull(),
            logger);
        return builder.Build();
    }

    private static BinaryArray BuildBinaryAttributeArray(ImmutableArray<Feature> features, string fieldName, ILogger? logger = null)
    {
        var builder = new BinaryArray.Builder();
        ForEachAttribute(features, fieldName,
            v =>
            {
                if (v is byte[] bytes)
                {
                    builder.Append(bytes);
                }
                else if (v is string s)
                {
                    // BYTEA attributes stored in the JSONB column are serialised as base64
                    // strings during JSON round-tripping. Decode them back to bytes.
                    try
                    {
                        builder.Append(Convert.FromBase64String(s));
                    }
                    catch (FormatException ex)
                    {
                        // Not valid base64 — treat as null and log.
                        if (logger != null)
                        {
                            FeatureServerLog.GeoParquetConversionFailed(logger, fieldName, "String(non-base64)", ex);
                        }
                        builder.AppendNull();
                    }
                }
                else
                {
                    builder.AppendNull();
                }
            },
            () => builder.AppendNull(), logger);
        return builder.Build();
    }

    /// <summary>
    /// Builds an array for a runtime-computed attribute (no <see cref="FieldDefinition"/>).
    /// Dispatches by the Arrow type declared in the schema field.
    /// </summary>
    private static IArrowArray BuildRuntimeAttributeArray(
        ImmutableArray<Feature> features,
        Field field,
        ILogger? logger = null)
    {
        return field.DataType switch
        {
            DoubleType => BuildDoubleAttributeArray(features, field.Name, logger),
            FloatType => BuildFloatAttributeArray(features, field.Name, logger),
            Int32Type => BuildInt32AttributeArray(features, field.Name, logger),
            Int64Type => BuildInt64AttributeArray(features, field.Name, logger),
            BooleanType => BuildBooleanAttributeArray(features, field.Name, logger),
            Date32Type => BuildDate32AttributeArray(features, field.Name, logger),
            Time32Type => BuildTime32AttributeArray(features, field.Name, logger),
            TimestampType => BuildTimestampAttributeArray(features, field.Name, logger),
            _ => BuildStringAttributeArray(features, field.Name, logger)
        };
    }

    private static StringArray BuildStringAttributeArray(ImmutableArray<Feature> features, string fieldName, ILogger? logger = null)
    {
        var builder = new StringArray.Builder();
        ForEachAttribute(features, fieldName, v => builder.Append(v.ToString() ?? string.Empty), () => builder.AppendNull(), logger);
        return builder.Build();
    }

    private static DateTime? TryConvertDateTimeValue(object? value)
    {
        return value switch
        {
            null => null,
            DateTime dateTime => NormalizeDateTime(dateTime),
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
            DateOnly dateOnly => DateTime.SpecifyKind(dateOnly.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            JsonElement element => TryConvertDateTimeElement(element),
            string stringValue when DateTimeOffset.TryParse(
                stringValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedDateTimeOffset) => parsedDateTimeOffset.UtcDateTime,
            string stringValue when DateOnly.TryParse(
                stringValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsedDateOnly) => DateTime.SpecifyKind(parsedDateOnly.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            byte byteValue => DateTimeOffset.FromUnixTimeMilliseconds(byteValue).UtcDateTime,
            short shortValue => DateTimeOffset.FromUnixTimeMilliseconds(shortValue).UtcDateTime,
            int intValue => DateTimeOffset.FromUnixTimeMilliseconds(intValue).UtcDateTime,
            long longValue => DateTimeOffset.FromUnixTimeMilliseconds(longValue).UtcDateTime,
            float floatValue => DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(floatValue, CultureInfo.InvariantCulture)).UtcDateTime,
            double doubleValue => DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(doubleValue, CultureInfo.InvariantCulture)).UtcDateTime,
            decimal decimalValue => DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(decimalValue, CultureInfo.InvariantCulture)).UtcDateTime,
            _ => null
        };
    }

    private static int? TryConvertTimeMilliseconds(object? value)
    {
        return value switch
        {
            null => null,
            TimeOnly timeOnly => checked((int)(timeOnly.Ticks / TimeSpan.TicksPerMillisecond)),
            TimeSpan timeSpan => checked((int)(timeSpan.Ticks / TimeSpan.TicksPerMillisecond)),
            DateTime dateTime => checked((int)(TimeOnly.FromDateTime(dateTime).Ticks / TimeSpan.TicksPerMillisecond)),
            DateTimeOffset dateTimeOffset => checked((int)(TimeOnly.FromDateTime(dateTimeOffset.UtcDateTime).Ticks / TimeSpan.TicksPerMillisecond)),
            JsonElement element => TryConvertTimeMillisecondsElement(element),
            string stringValue when TimeOnly.TryParse(
                stringValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsedTimeOnly) => checked((int)(parsedTimeOnly.Ticks / TimeSpan.TicksPerMillisecond)),
            string stringValue when TimeSpan.TryParse(
                stringValue,
                CultureInfo.InvariantCulture,
                out var parsedTimeSpan) => checked((int)(parsedTimeSpan.Ticks / TimeSpan.TicksPerMillisecond)),
            _ => null
        };
    }

    private static DateTime NormalizeDateTime(DateTime value)
    {
        return value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
    }

    private static DateTime? TryConvertDateTimeElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => TryConvertDateTimeValue(element.GetString()),
            JsonValueKind.Number when element.TryGetInt64(out var milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime,
            JsonValueKind.Number when element.TryGetDouble(out var milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(milliseconds, CultureInfo.InvariantCulture)).UtcDateTime,
            _ => null
        };
    }

    private static int? TryConvertTimeMillisecondsElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => TryConvertTimeMilliseconds(element.GetString()),
            _ => null
        };
    }

    /// <summary>
    /// Checks whether the geometry contains any non-NaN Z value by walking
    /// coordinate sequences directly, avoiding the intermediate Coordinate[]
    /// allocation that <see cref="Geometry.Coordinates"/> would produce.
    /// </summary>
    private static bool GeometryHasZ(Geometry geometry)
    {
        for (var g = 0; g < geometry.NumGeometries; g++)
        {
            var part = geometry.GetGeometryN(g);
            if (part is Point point)
            {
                if (point.CoordinateSequence.Dimension > 2 &&
                    !double.IsNaN(point.CoordinateSequence.GetOrdinate(0, Ordinate.Z)))
                {
                    return true;
                }

                continue;
            }

            if (part is LineString lineString)
            {
                if (SequenceHasZ(lineString.CoordinateSequence)) return true;
                continue;
            }

            if (part is Polygon polygon)
            {
                if (SequenceHasZ(polygon.ExteriorRing.CoordinateSequence)) return true;
                for (var h = 0; h < polygon.NumInteriorRings; h++)
                {
                    if (SequenceHasZ(polygon.GetInteriorRingN(h).CoordinateSequence)) return true;
                }
            }
        }

        return false;
    }

    private static bool SequenceHasZ(CoordinateSequence sequence)
    {
        if (sequence.Dimension <= 2) return false;
        for (var i = 0; i < sequence.Count; i++)
        {
            if (!double.IsNaN(sequence.GetOrdinate(i, Ordinate.Z))) return true;
        }

        return false;
    }

    private static WKBReader GetWkbReader()
    {
        _wkbReader ??= new WKBReader();
        return _wkbReader;
    }

}

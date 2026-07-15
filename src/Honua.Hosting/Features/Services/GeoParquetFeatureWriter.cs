// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using ParquetSharp.Arrow;

namespace Honua.Infrastructure.Services;

/// <summary>
/// Shared GeoParquet feature encoder used by every protocol adapter that exports
/// cloud-native Parquet (GeoServices <c>f=parquet</c>, OData <c>$format=parquet</c>).
/// Encodes a canonical <see cref="QueryResult{Feature}"/> into a spec-compliant
/// GeoParquet 1.1.0 byte payload so the wire format lives in one place rather than
/// being reimplemented per protocol.
/// </summary>
public static partial class GeoParquetFeatureWriter
{
    // Constants for GeoParquet format
    private const string GeometryColumnName = "geometry";

    // GeoParquet 1.1 spatial-pruning covering column (a per-row bounding box) so a reader can
    // skip row groups whose bbox does not intersect a query window without decoding geometry.
    private const string BboxColumnName = "bbox";
    private const string BboxXMinFieldName = "xmin";
    private const string BboxYMinFieldName = "ymin";
    private const string BboxXMaxFieldName = "xmax";
    private const string BboxYMaxFieldName = "ymax";

    private const string GeoParquetVersion = "1.1.0";
    private const string GeometryEncoding = "WKB";
    private const string GeoMetadataKey = "geo";

    /// <summary>
    /// GeoParquet content type emitted by <see cref="FormatAsGeoParquet"/>.
    /// </summary>
    public const string ContentType = "application/vnd.apache.parquet";

    [ThreadStatic]
    private static WKBWriter? _wkbWriter2D;

    [ThreadStatic]
    private static WKBWriter? _wkbWriter3D;

    /// <summary>
    /// Formats a canonical query result as GeoParquet using Metadata v2 resource metadata.
    /// </summary>
    /// <param name="result">Canonical query result to encode.</param>
    /// <param name="resource">Metadata v2 resource describing the layer schema.</param>
    /// <param name="objectIdFieldName">Resolved object-id field name for the layer.</param>
    /// <param name="returnGeometry">Whether to include the geometry column.</param>
    /// <param name="outputSrid">Output SRID for CRS metadata (4326 only when geometry is included).</param>
    /// <param name="returnZ">Whether to retain Z ordinates.</param>
    /// <param name="returnM">Whether M ordinates were requested (rejected for GeoParquet).</param>
    /// <param name="geometryLimits">Geometry output limits.</param>
    /// <param name="outFields">Requested output fields, or null / ["*"] for all.</param>
    /// <param name="logger">Optional logger for conversion diagnostics.</param>
    /// <returns>The GeoParquet payload and its content type.</returns>
    public static (byte[] response, string contentType) FormatAsGeoParquet(
        QueryResult<Feature> result,
        MetadataV2Resource resource,
        string objectIdFieldName,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        string[]? outFields = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var features = result.Items;
        var includeGeometry = returnGeometry && HasGeometry(resource);
        var srid = outputSrid ?? resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;

        EnsureSupportedCloudNativeGeometrySrid(includeGeometry, srid, "GeoParquet");
        EnsureSupportedCloudNativeGeometryMeasures(includeGeometry, returnM, "GeoParquet");

        if (features.Length == 0)
        {
            return CreateEmptyGeoParquet(resource, objectIdFieldName, returnGeometry, outFields, outputSrid, returnZ);
        }

        var runtimeFields = DetectRuntimeFields(features, resource);

        BinaryArray? geometryArray = null;
        StructArray? bboxArray = null;
        bool anyGeometryHasZ = false;
        if (returnGeometry && HasGeometry(resource))
        {
            (geometryArray, bboxArray, anyGeometryHasZ) = BuildGeometryArray(
                features,
                srid,
                returnZ,
                returnM,
                geometryLimits,
                logger);
        }

        var (schema, fieldsToInclude, resolvedObjectIdFieldName) = BuildSchema(
            resource, objectIdFieldName, returnGeometry, outFields, outputSrid,
            advertiseZ: anyGeometryHasZ,
            runtimeFields);

        var arrays = BuildArrays(
            features,
            schema,
            returnGeometry,
            geometryArray,
            bboxArray,
            resolvedObjectIdFieldName,
            fieldsToInclude,
            logger);

        using var recordBatch = new RecordBatch(schema, arrays, features.Length);

        return (WriteArrowParquet(schema, recordBatch), ContentType);
    }

    // Single chokepoint for the native ParquetSharp Arrow encoder. The ParquetSharp NuGet
    // package ships a glibc-only native binary (ParquetSharpNative) that cannot load on the
    // Alpine/musl runtime image, so the first construction of ArrowWriterPropertiesBuilder /
    // FileWriter raises DllNotFoundException (possibly wrapped in TypeInitializationException).
    // Translate that into a typed ParquetRuntimeUnavailableException so protocol adapters can
    // return a clean 501 capability response instead of an unhandled 500 (honua-server#1942).
    private static byte[] WriteArrowParquet(Schema schema, RecordBatch? recordBatch)
    {
        try
        {
            using var stream = new MemoryStream();
            // Construct and dispose the builder itself explicitly (rather than chaining off
            // the fluent StoreSchema() return value) so the builder instance is always
            // disposed even if StoreSchema() were ever changed to return a different object.
            using var arrowWriterPropertiesBuilder = new ArrowWriterPropertiesBuilder();
            arrowWriterPropertiesBuilder.StoreSchema();
            using var arrowWriterProperties = arrowWriterPropertiesBuilder.Build();
            using (var writer = new FileWriter(stream, schema, null, arrowWriterProperties, true))
            {
                if (recordBatch is not null)
                {
                    writer.WriteRecordBatch(recordBatch);
                }

                writer.Close();
            }

            return stream.ToArray();
        }
        catch (Exception ex) when (ParquetRuntimeUnavailableException.IsNativeLoadFailure(ex))
        {
            throw new ParquetRuntimeUnavailableException(ex);
        }
    }

    /// <summary>
    /// Resolves the schema fields included in a GeoParquet / GeoArrow export.
    /// </summary>
    public static List<MetadataV2Field> ResolveSelectedFields(
        MetadataV2Resource resource,
        string objectIdFieldName,
        string[]? outFields)
    {
        var includeAllFields = outFields == null || outFields.Length == 0 ||
            (outFields.Length == 1 && outFields[0].Equals("*", StringComparison.Ordinal));

        HashSet<string>? requestedFields = null;
        if (!includeAllFields)
        {
            requestedFields = new HashSet<string>(outFields!, StringComparer.OrdinalIgnoreCase)
            {
                objectIdFieldName
            };
        }

        var selectedFields = resource.SchemaFields
            .Where(field => !IsGeometryField(field))
            .Where(field => !field.Hidden)
            .Where(field => includeAllFields || requestedFields!.Contains(field.Name))
            .ToList();

        if (!selectedFields.Any(field => field.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase)))
        {
            selectedFields.Insert(0, new MetadataV2Field
            {
                Name = objectIdFieldName,
                Type = MetadataV2FieldType.BigInteger,
                Nullable = false,
                SqlType = "BIGINT"
            });
        }

        return selectedFields;
    }

    private static (byte[] response, string contentType) CreateEmptyGeoParquet(
        MetadataV2Resource resource,
        string objectIdFieldName,
        bool returnGeometry,
        string[]? outFields,
        int? outputSrid,
        bool returnZ)
    {
        var (schema, _, _) = BuildSchema(resource, objectIdFieldName, returnGeometry, outFields, outputSrid, returnZ, isEmpty: true);

        return (WriteArrowParquet(schema, recordBatch: null), ContentType);
    }

    /// <summary>
    /// Builds the Arrow schema and resolves which attribute fields to include.
    /// Shared by both populated and empty GeoParquet paths.
    /// </summary>
    private static (Schema schema, List<MetadataV2Field> fieldsToInclude, string objectIdFieldName) BuildSchema(
        MetadataV2Resource resource,
        string objectIdFieldName,
        bool returnGeometry,
        string[]? outFields,
        int? outputSrid,
        bool advertiseZ,
        IReadOnlyList<(string name, IArrowType type)>? runtimeFields = null,
        bool isEmpty = false)
    {
        var resolvedFields = ResolveSelectedFields(resource, objectIdFieldName, outFields);

        var fieldsToInclude = resolvedFields
            .Where(f => !f.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var schemaFields = new List<Field>
        {
            new Field(objectIdFieldName, new Int64Type(), false)
        };

        if (returnGeometry && HasGeometry(resource))
        {
            schemaFields.Add(new Field(GeometryColumnName, new BinaryType(), true));
        }

        foreach (var field in fieldsToInclude)
        {
            schemaFields.Add(new Field(field.Name, MapToArrowType(field), field.Nullable));
        }

        if (runtimeFields != null)
        {
            var includeAllFields = outFields == null || outFields.Length == 0 ||
                                  (outFields.Length == 1 && outFields[0].Equals("*", StringComparison.Ordinal));
            foreach (var (name, type) in runtimeFields)
            {
                if (includeAllFields || outFields!.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    schemaFields.Add(new Field(name, type, nullable: true));
                }
            }
        }

        // GeoParquet 1.1 covering bbox column. Declared last so attribute/runtime ordering stays
        // stable; the `geo` metadata `covering` maps xmin/ymin/xmax/ymax onto this struct.
        if (returnGeometry && HasGeometry(resource))
        {
            schemaFields.Add(new Field(BboxColumnName, CreateBboxStructType(), nullable: true));
        }

        var schema = new Schema(schemaFields, BuildGeoParquetMetadata(resource, returnGeometry, outputSrid, advertiseZ, isEmpty));
        return (schema, fieldsToInclude, objectIdFieldName);
    }

    /// <summary>
    /// Builds GeoParquet metadata following the specification.
    /// </summary>
    private static Dictionary<string, string> BuildGeoParquetMetadata(
        MetadataV2Resource resource,
        bool returnGeometry,
        int? outputSrid,
        bool advertiseZ,
        bool isEmpty = false)
    {
        var metadata = new Dictionary<string, string>();

        if (!returnGeometry || !HasGeometry(resource))
        {
            return metadata;
        }

        var srid = outputSrid ?? resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        EnsureSupportedCloudNativeGeometrySrid(includeGeometry: true, srid, "GeoParquet");

        var geometryTypesPart = isEmpty
            ? "[]"
            : $"[\"{MapGeometryTypeToGeoParquet(resource.ReadGeometryType(), advertiseZ)}\"]";

        // GeoParquet 1.1 "covering" declares the per-row bbox column used for spatial pruning.
        // The paths point at the fields of the emitted `bbox` struct column so a reader can map
        // xmin/ymin/xmax/ymax without decoding geometry (honua-server#2843).
        var coveringPart =
            $@",""covering"":{{""bbox"":{{""{BboxXMinFieldName}"":[""{BboxColumnName}"",""{BboxXMinFieldName}""],""{BboxYMinFieldName}"":[""{BboxColumnName}"",""{BboxYMinFieldName}""],""{BboxXMaxFieldName}"":[""{BboxColumnName}"",""{BboxXMaxFieldName}""],""{BboxYMaxFieldName}"":[""{BboxColumnName}"",""{BboxYMaxFieldName}""]}}}}";
        var geoJson = $@"{{""version"":""{GeoParquetVersion}"",""primary_column"":""{GeometryColumnName}"",""columns"":{{""{GeometryColumnName}"":{{""encoding"":""{GeometryEncoding}"",""geometry_types"":{geometryTypesPart}{coveringPart}}}}}}}";

        metadata[GeoMetadataKey] = geoJson;

        return metadata;
    }

    /// <summary>
    /// Validates that a cloud-native geometry export uses the only spec-compliant SRID (EPSG:4326).
    /// </summary>
    public static void EnsureSupportedCloudNativeGeometrySrid(bool includeGeometry, int srid, string formatLabel)
    {
        if (!includeGeometry || srid == 4326)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{formatLabel} geometry output only supports EPSG:4326. Requested SRID {srid} cannot be encoded with spec-compliant CRS metadata.");
    }

    /// <summary>
    /// Validates that a cloud-native geometry export does not request unsupported M ordinates.
    /// </summary>
    public static void EnsureSupportedCloudNativeGeometryMeasures(bool includeGeometry, bool returnM, string formatLabel)
    {
        if (!includeGeometry || !returnM)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{formatLabel} geometry output does not support returnM=true. GeoParquet 1.1.0 only supports XY and XYZ geometries.");
    }

    /// <summary>
    /// Detects runtime-computed attributes present in the result set but not declared in the
    /// resource schema (e.g. the "distance" field injected by KNN queries when returnDistance=true).
    /// Internal fields prefixed with "__" are excluded.
    /// </summary>
    public static List<(string name, IArrowType type)> DetectRuntimeFields(
        ImmutableArray<Feature> features,
        MetadataV2Resource resource)
    {
        var result = new List<(string name, IArrowType type)>();
        if (features.Length == 0) return result;

        var resourceFieldNames = new HashSet<string>(
            resource.SchemaFields.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var seenRuntimeFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var feature in features)
        {
            foreach (var (key, value) in feature.Attributes)
            {
                if (seenRuntimeFields.Contains(key) || resourceFieldNames.Contains(key)) continue;
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
    /// Maps metadata v2 geometry type to GeoParquet geometry type string.
    /// </summary>
    public static string MapGeometryTypeToGeoParquet(MetadataV2GeometryType geometryType, bool returnZ)
    {
        var baseType = geometryType switch
        {
            MetadataV2GeometryType.Point => "Point",
            MetadataV2GeometryType.LineString => "LineString",
            MetadataV2GeometryType.Polygon => "Polygon",
            MetadataV2GeometryType.MultiPoint => "MultiPoint",
            MetadataV2GeometryType.MultiLineString => "MultiLineString",
            MetadataV2GeometryType.MultiPolygon => "MultiPolygon",
            MetadataV2GeometryType.GeometryCollection => "GeometryCollection",
            _ => "Geometry"
        };

        return returnZ ? $"{baseType} Z" : baseType;
    }

    /// <summary>
    /// Maps metadata v2 field metadata to Arrow data type.
    /// IMPORTANT: keep in sync with the matching attribute array builders which select the
    /// matching typed builder for the same SQL type strings.
    /// </summary>
    private static IArrowType MapToArrowType(MetadataV2Field field)
    {
        return ResolveSqlType(field).ToLowerInvariant() switch
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
            var sqlType when sqlType.StartsWith("varchar", StringComparison.OrdinalIgnoreCase) => new StringType(),
            var sqlType when sqlType.StartsWith("char", StringComparison.OrdinalIgnoreCase) => new StringType(),
            var sqlType when sqlType.StartsWith("text", StringComparison.OrdinalIgnoreCase) => new StringType(),
            var sqlType when sqlType.StartsWith("numeric", StringComparison.OrdinalIgnoreCase) => new DoubleType(),
            var sqlType when sqlType.StartsWith("decimal", StringComparison.OrdinalIgnoreCase) => new DoubleType(),
            _ => new StringType()
        };
    }

    private static string ResolveSqlType(MetadataV2Field field)
    {
        if (!string.IsNullOrWhiteSpace(field.SqlType))
        {
            return field.SqlType;
        }

        return field.Type switch
        {
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
    }

    private static bool HasGeometry(MetadataV2Resource resource)
        => resource.ReadGeometryType() != MetadataV2GeometryType.None;

    private static bool IsGeometryField(MetadataV2Field field)
        => field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography;

    /// <summary>
    /// Builds Arrow arrays for each column.
    /// The geometry column is pre-built and passed in (null when geometry is excluded).
    /// </summary>
    private static IArrowArray[] BuildArrays(
        ImmutableArray<Feature> features,
        Schema schema,
        bool returnGeometry,
        BinaryArray? geometryArray,
        StructArray? bboxArray,
        string objectIdFieldName,
        List<MetadataV2Field> fieldsToInclude,
        ILogger? logger = null)
    {
        var arrays = new List<IArrowArray>();

        foreach (var field in schema.FieldsList)
        {
            if (field.Name == objectIdFieldName)
            {
                arrays.Add(BuildObjectIdArray(features, objectIdFieldName));
            }
            else if (field.Name == GeometryColumnName && returnGeometry && geometryArray != null)
            {
                arrays.Add(geometryArray);
            }
            else if (field.Name == BboxColumnName && returnGeometry && bboxArray != null)
            {
                arrays.Add(bboxArray);
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
                    arrays.Add(BuildRuntimeAttributeArray(features, field, logger));
                }
            }
        }

        return arrays.ToArray();
    }

    /// <summary>
    /// Builds Int64 array for object IDs directly from features without intermediate allocation
    /// </summary>
    private static Int64Array BuildObjectIdArray(ImmutableArray<Feature> features, string objectIdFieldName)
    {
        var builder = new Int64Array.Builder();
        foreach (var feature in features)
        {
            builder.Append(ResolveObjectIdValue(feature, objectIdFieldName));
        }
        return builder.Build();
    }

    /// <summary>
    /// Resolves the object-id value for a feature, preferring the resolved object-id
    /// attribute (numeric-normalized) and falling back to the canonical feature id.
    /// </summary>
    public static long ResolveObjectIdValue(Feature feature, string objectIdFieldName)
    {
        if (!string.IsNullOrWhiteSpace(objectIdFieldName) &&
            feature.Attributes.TryGetValue(objectIdFieldName, out var value) &&
            TryNormalizeObjectId(value, out var objectId))
        {
            return objectId;
        }

        return feature.Id;
    }

    private static bool TryNormalizeObjectId(object? value, out long objectId)
    {
        objectId = 0;
        switch (value)
        {
            case long number:
                objectId = number;
                return true;
            case int number:
                objectId = number;
                return true;
            case short number:
                objectId = number;
                return true;
            case byte number:
                objectId = number;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var parsed):
                objectId = parsed;
                return true;
            case string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                objectId = parsed;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Builds the WKB geometry array and the parallel GeoParquet 1.1 covering bbox column,
    /// and tracks whether any geometry retains Z coordinates. The per-row bbox is the envelope
    /// of the emitted (output-SRID, limit-applied) geometry so it stays consistent with the WKB;
    /// null / empty geometries yield a null bbox row.
    /// </summary>
    private static (BinaryArray array, StructArray bbox, bool anyHasZ) BuildGeometryArray(
        ImmutableArray<Feature> features,
        int outputSrid,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        ILogger? logger = null)
    {
        var builder = new BinaryArray.Builder();
        var xMinBuilder = new DoubleArray.Builder();
        var yMinBuilder = new DoubleArray.Builder();
        var xMaxBuilder = new DoubleArray.Builder();
        var yMaxBuilder = new DoubleArray.Builder();
        var bboxValidity = new ArrowBuffer.BitmapBuilder();
        var bboxNullCount = 0;
        var anyHasZ = false;

        foreach (var feature in features)
        {
            var (wkb, envelope, hasZ) = ProcessGeometryCore(feature.Geometry, outputSrid, geometryLimits, returnZ, returnM, feature.Id, logger);
            anyHasZ |= hasZ;
            if (wkb != null && wkb.Length > 0)
            {
                builder.Append(wkb);
            }
            else
            {
                builder.AppendNull();
            }

            if (envelope != null && !envelope.IsNull)
            {
                xMinBuilder.Append(envelope.MinX);
                yMinBuilder.Append(envelope.MinY);
                xMaxBuilder.Append(envelope.MaxX);
                yMaxBuilder.Append(envelope.MaxY);
                bboxValidity.Append(true);
            }
            else
            {
                xMinBuilder.AppendNull();
                yMinBuilder.AppendNull();
                xMaxBuilder.AppendNull();
                yMaxBuilder.AppendNull();
                bboxValidity.Append(false);
                bboxNullCount++;
            }
        }

        var bboxArray = BuildBboxStructArray(
            features.Length,
            xMinBuilder.Build(),
            yMinBuilder.Build(),
            xMaxBuilder.Build(),
            yMaxBuilder.Build(),
            bboxValidity.Build(),
            bboxNullCount);

        return (builder.Build(), bboxArray, anyHasZ);
    }

    /// <summary>
    /// Arrow struct type for the GeoParquet covering bbox column: four double ordinate fields.
    /// A single definition is reused for both the schema field and the array so the declared
    /// column type and the data match exactly.
    /// </summary>
    private static StructType CreateBboxStructType() => new(new List<Field>
    {
        new Field(BboxXMinFieldName, new DoubleType(), nullable: true),
        new Field(BboxYMinFieldName, new DoubleType(), nullable: true),
        new Field(BboxXMaxFieldName, new DoubleType(), nullable: true),
        new Field(BboxYMaxFieldName, new DoubleType(), nullable: true)
    });

    /// <summary>
    /// Assembles the covering bbox <see cref="StructArray"/> from its four ordinate child arrays
    /// and a validity bitmap that marks rows with null / empty geometry as null.
    /// </summary>
    private static StructArray BuildBboxStructArray(
        int length,
        DoubleArray xMin,
        DoubleArray yMin,
        DoubleArray xMax,
        DoubleArray yMax,
        ArrowBuffer nullBitmap,
        int nullCount)
    {
        var children = new IArrowArray[] { xMin, yMin, xMax, yMax };
        return new StructArray(CreateBboxStructType(), length, children, nullBitmap, nullCount);
    }

    /// <summary>
    /// Reads an attribute value from a feature, returning null when absent.
    /// </summary>
    public static object? GetAttributeValue(Feature feature, string fieldName)
    {
        return feature.Attributes.TryGetValue(fieldName, out var value) ? value : null;
    }

    /// <summary>
    /// Processes a WKB geometry payload into the spec-compliant output WKB, applying limits and dimension filtering.
    /// </summary>
    public static byte[]? ProcessGeometry(
        byte[]? geometryBytes,
        int outputSrid,
        GeometryLimits geometryLimits,
        bool returnZ,
        bool returnM,
        long featureId = 0,
        ILogger? logger = null)
    {
        var (wkb, _, _) = ProcessGeometryCore(geometryBytes, outputSrid, geometryLimits, returnZ, returnM, featureId, logger);
        return wkb;
    }

    private static (byte[]? wkb, Envelope? envelope, bool hasZ) ProcessGeometryCore(
        byte[]? geometryBytes,
        int outputSrid,
        GeometryLimits geometryLimits,
        bool returnZ,
        bool returnM,
        long featureId = 0,
        ILogger? logger = null)
    {
        if (geometryBytes == null || geometryBytes.Length == 0)
        {
            return (null, null, false);
        }

        Geometry? geometry;
        try
        {
            geometry = WkbReaderCache.Get().Read(geometryBytes);
        }
        catch (Exception ex) when (ex is ParseException or FormatException)
        {
            if (logger != null)
            {
                Log.GeoParquetCorruptGeometry(logger, featureId, geometryBytes.Length, ex);
            }

            return (null, null, false);
        }

        if (geometry == null)
        {
            return (null, null, false);
        }

        geometry.SRID = outputSrid;
        geometry = GeometryOutputProcessor.ApplyLimits(geometry, geometryLimits);
        if (geometry == null)
        {
            return (null, null, false);
        }

        // GeoParquet 1.1.0 only supports XY and XYZ — always strip M values.
        geometry = GeometryOutputProcessor.ApplyDimensionFilter(geometry, returnZ, includeM: false);

        var hasZ = GeometryHasZ(geometry);
        var writer = GetWkbWriter(hasZ);

        // Envelope of the emitted geometry (output SRID, limits applied) feeds the covering bbox
        // column so the stored bbox exactly matches the WKB. Empty geometries have a null envelope
        // and therefore a null bbox row.
        var envelope = geometry.EnvelopeInternal;
        return (writer.Write(geometry), envelope, hasZ);
    }

    /// <summary>
    /// Builds attribute array for a specific field.
    /// IMPORTANT: keep in sync with the matching schema type mapper which declares the
    /// Arrow schema type for the same SQL type strings.
    /// </summary>
    private static IArrowArray BuildAttributeArray(
        ImmutableArray<Feature> features,
        string fieldName,
        MetadataV2Field fieldDef,
        ILogger? logger = null)
    {
        var sqlType = ResolveSqlType(fieldDef).ToLowerInvariant();
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
            var resolved when resolved.StartsWith("numeric", StringComparison.OrdinalIgnoreCase) => BuildDoubleAttributeArray(features, fieldName, logger),
            var resolved when resolved.StartsWith("decimal", StringComparison.OrdinalIgnoreCase) => BuildDoubleAttributeArray(features, fieldName, logger),
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
                        Log.GeoParquetConversionFailed(logger, fieldName, value.GetType().Name, ex);
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
                            Log.GeoParquetConversionFailed(logger, fieldName, "String(non-base64)", ex);
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
    /// Builds an array for a runtime-computed attribute absent from the resource schema.
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

    private static WKBWriter GetWkbWriter(bool emitZ)
    {
        if (emitZ)
        {
            _wkbWriter3D ??= new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: false);
            return _wkbWriter3D;
        }

        _wkbWriter2D ??= new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: false, emitM: false);
        return _wkbWriter2D;
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7401,
            Level = LogLevel.Warning,
            Message = "GeoParquet conversion failed for field '{FieldName}' (value type: {ValueType}), value treated as null")]
        public static partial void GeoParquetConversionFailed(ILogger logger, string fieldName, string valueType, Exception exception);

        [LoggerMessage(
            EventId = 7402,
            Level = LogLevel.Warning,
            Message = "GeoParquet export: corrupt WKB for feature {FeatureId} ({WkbLength} bytes), geometry treated as null")]
        public static partial void GeoParquetCorruptGeometry(ILogger logger, long featureId, int wkbLength, Exception exception);
    }
}

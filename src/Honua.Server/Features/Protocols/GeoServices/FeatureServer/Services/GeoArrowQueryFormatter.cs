// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Formats query results as Arrow IPC Stream with GeoArrow metadata.
/// </summary>
internal sealed class GeoArrowQueryFormatter
{
    private const string GeometryColumnName = "geometry";
    private const string GeoArrowExtensionName = "geoarrow.wkb";
    private const string ArrowExtensionNameKey = "ARROW:extension:name";
    private const string ArrowExtensionMetadataKey = "ARROW:extension:metadata";
    private const string GeoMetadataKey = "geo";

    /// <summary>
    /// Formats query result as Arrow IPC Stream with GeoArrow metadata.
    /// </summary>
    public static async Task<(byte[] response, string contentType)> FormatAsGeoArrowAsync(
        QueryResult<Feature> result,
        LayerDefinition layer,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        string[]? outFields = null,
        CancellationToken cancellationToken = default)
    {
        var selectedFields = GeoParquetQueryFormatter.ResolveSelectedFields(layer, outFields);
        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(layer);
        var includeGeometry = returnGeometry && layer.HasGeometry;
        var srid = outputSrid ?? layer.SpatialReference.Wkid;
        GeoParquetQueryFormatter.EnsureSupportedCloudNativeGeometrySrid(includeGeometry, srid, "GeoArrow");
        GeoParquetQueryFormatter.EnsureSupportedCloudNativeGeometryMeasures(includeGeometry, returnM, "GeoArrow");
        var features = result.Items;
        var isEmpty = features.Length == 0;

        // Detect runtime-computed attributes (e.g. "distance" from KNN queries)
        // that exist in the result set but are not declared in the layer schema.
        var runtimeFields = GeoParquetQueryFormatter.DetectRuntimeFields(features, layer);

        var schema = BuildSchema(selectedFields, includeGeometry, layer, srid, returnZ, isEmpty, runtimeFields, outFields);
        var recordBatch = BuildRecordBatch(
            features,
            selectedFields,
            objectIdFieldName,
            includeGeometry,
            srid,
            geometryLimits,
            returnZ,
            returnM,
            schema,
            runtimeFields);

        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, schema, leaveOpen: true))
        {
            await writer.WriteRecordBatchAsync(recordBatch, cancellationToken).ConfigureAwait(false);
            await writer.WriteEndAsync(cancellationToken).ConfigureAwait(false);
        }

        return (stream.ToArray(), "application/vnd.apache.arrow.stream");
    }

    private static Apache.Arrow.Schema BuildSchema(
        List<FieldDefinition> selectedFields,
        bool includeGeometry,
        LayerDefinition layer,
        int srid,
        bool returnZ,
        bool isEmpty,
        List<(string name, IArrowType type)> runtimeFields,
        string[]? outFields)
    {
        var fields = new List<Apache.Arrow.Field>(selectedFields.Count + (includeGeometry ? 1 : 0));

        foreach (var field in selectedFields)
        {
            fields.Add(CreateArrowField(field));
        }

        if (includeGeometry)
        {
            var extensionMetadata = BuildExtensionMetadata(layer, srid, returnZ, isEmpty);
            var geometryField = new Apache.Arrow.Field(
                GeometryColumnName,
                BinaryType.Default,
                nullable: true,
                new Dictionary<string, string>
                {
                    [ArrowExtensionNameKey] = GeoArrowExtensionName,
                    [ArrowExtensionMetadataKey] = extensionMetadata
                });
            fields.Add(geometryField);
        }

        // Append runtime-computed attributes (e.g. "distance" from KNN queries) that are
        // not declared in the layer schema but appear in feature.Attributes.
        // Only include when outFields is omitted / "*", or the field was explicitly requested,
        // to match the filtering behavior of JSON/GeoJSON/PBF/GeoParquet formatters.
        if (runtimeFields.Count > 0)
        {
            var includeAllFields = outFields == null || outFields.Length == 0 ||
                                  (outFields.Length == 1 && outFields[0].Equals("*", StringComparison.Ordinal));
            foreach (var (name, type) in runtimeFields)
            {
                if (includeAllFields || outFields!.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    fields.Add(new Apache.Arrow.Field(name, type, nullable: true));
                }
            }
        }

        var schemaMetadata = BuildSchemaMetadata(layer, includeGeometry, srid, returnZ, isEmpty);
        return new Apache.Arrow.Schema(fields, schemaMetadata);
    }

    private static Apache.Arrow.Field CreateArrowField(FieldDefinition field)
    {
        // Note: Date/Time type mappings intentionally diverge from GeoParquet.
        // Arrow IPC is consumed by analytics clients (PyArrow, DuckDB) that prefer
        // Timestamp over Date32 for dates, and Time64(μs) over Time32(ms) for times,
        // because these types offer higher fidelity and are the default in most Arrow
        // ecosystems. GeoParquet uses Date32/Time32 to match Parquet's native types.
        IArrowType arrowType = field.Type switch
        {
            FieldType.BigInteger => Int64Type.Default,
            FieldType.Integer => Int32Type.Default,
            FieldType.Float => FloatType.Default,
            FieldType.Double => DoubleType.Default,
            FieldType.Boolean => BooleanType.Default,
            FieldType.DateTime or FieldType.Date => new TimestampType(TimeUnit.Millisecond, "UTC"),
            FieldType.Time => new Time64Type(TimeUnit.Microsecond),
            FieldType.Binary => BinaryType.Default,
            _ => StringType.Default
        };

        return new Apache.Arrow.Field(field.Name, arrowType, field.Nullable);
    }

    private static RecordBatch BuildRecordBatch(
        IReadOnlyList<Feature> features,
        List<FieldDefinition> selectedFields,
        string objectIdFieldName,
        bool includeGeometry,
        int outputSrid,
        GeometryLimits geometryLimits,
        bool returnZ,
        bool returnM,
        Apache.Arrow.Schema schema,
        IReadOnlyList<(string name, IArrowType type)> runtimeFields)
    {
        var arrays = new List<IArrowArray>(schema.FieldsList.Count);

        foreach (var field in selectedFields)
        {
            var isObjectIdField = field.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase);
            arrays.Add(BuildAttributeArray(features, field, isObjectIdField));
        }

        if (includeGeometry)
        {
            arrays.Add(BuildGeometryArray(features, outputSrid, geometryLimits, returnZ, returnM));
        }

        // Build arrays for runtime-computed fields (only those included in the schema).
        // Match schema field names to ensure we only build arrays for fields that passed
        // the outFields filter in BuildSchema.
        var schemaFieldNames = new HashSet<string>(
            schema.FieldsList.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, type) in runtimeFields)
        {
            if (schemaFieldNames.Contains(name))
            {
                arrays.Add(BuildRuntimeArray(features, name, type));
            }
        }

        return new RecordBatch(schema, arrays, features.Count);
    }

    private static IArrowArray BuildRuntimeArray(
        IReadOnlyList<Feature> features,
        string fieldName,
        IArrowType arrowType)
    {
        // Dispatch by Arrow type and build using the same converters as layer-defined fields.
        return arrowType switch
        {
            DoubleType => BuildDoubleArrayByName(features, fieldName),
            FloatType => BuildFloatArrayByName(features, fieldName),
            Int32Type => BuildInt32ArrayByName(features, fieldName),
            Int64Type => BuildInt64ArrayByName(features, fieldName),
            BooleanType => BuildBooleanArrayByName(features, fieldName),
            TimestampType => BuildTimestampArrayByName(features, fieldName),
            _ => BuildStringArrayByName(features, fieldName)
        };
    }

    private static DoubleArray BuildDoubleArrayByName(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new DoubleArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertDouble(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
            if (value.HasValue) builder.Append(value.Value); else builder.AppendNull();
        }
        return builder.Build();
    }

    private static FloatArray BuildFloatArrayByName(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new FloatArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertFloat(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
            if (value.HasValue) builder.Append(value.Value); else builder.AppendNull();
        }
        return builder.Build();
    }

    private static Int32Array BuildInt32ArrayByName(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new Int32Array.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertInt32(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
            if (value.HasValue) builder.Append(value.Value); else builder.AppendNull();
        }
        return builder.Build();
    }

    private static Int64Array BuildInt64ArrayByName(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new Int64Array.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertInt64(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
            if (value.HasValue) builder.Append(value.Value); else builder.AppendNull();
        }
        return builder.Build();
    }

    private static BooleanArray BuildBooleanArrayByName(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new BooleanArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertBoolean(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
            if (value.HasValue) builder.Append(value.Value); else builder.AppendNull();
        }
        return builder.Build();
    }

    private static TimestampArray BuildTimestampArrayByName(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new TimestampArray.Builder(new TimestampType(TimeUnit.Millisecond, "UTC"));
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertDateTimeOffset(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
            if (value.HasValue) builder.Append(value.Value); else builder.AppendNull();
        }
        return builder.Build();
    }

    private static StringArray BuildStringArrayByName(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new StringArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = ConvertToStringValue(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
            if (value != null) builder.Append(value); else builder.AppendNull();
        }
        return builder.Build();
    }

    private static IArrowArray BuildAttributeArray(
        IReadOnlyList<Feature> features,
        FieldDefinition field,
        bool isObjectIdField)
    {
        return field.Type switch
        {
            FieldType.BigInteger => BuildInt64Array(features, field, isObjectIdField),
            FieldType.Integer => BuildInt32Array(features, field, isObjectIdField),
            FieldType.Float => BuildFloatArray(features, field),
            FieldType.Double => BuildDoubleArray(features, field),
            FieldType.Boolean => BuildBooleanArray(features, field),
            FieldType.DateTime or FieldType.Date => BuildTimestampArray(features, field),
            FieldType.Time => BuildTime64Array(features, field),
            FieldType.Binary => BuildBinaryArray(features, field),
            _ => BuildStringArray(features, field)
        };
    }

    private static Int64Array BuildInt64Array(IReadOnlyList<Feature> features, FieldDefinition field, bool isObjectIdField)
    {
        var builder = new Int64Array.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = GeoParquetQueryFormatter.GetAttributeValue(features[i], field.Name);
            var converted = TryConvertInt64(value);
            if (converted.HasValue)
            {
                builder.Append(converted.Value);
            }
            else if (isObjectIdField)
            {
                builder.Append(features[i].Id);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static Int32Array BuildInt32Array(IReadOnlyList<Feature> features, FieldDefinition field, bool isObjectIdField)
    {
        var builder = new Int32Array.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = GeoParquetQueryFormatter.GetAttributeValue(features[i], field.Name);
            var converted = TryConvertInt32(value);
            if (converted.HasValue)
            {
                builder.Append(converted.Value);
            }
            else if (isObjectIdField)
            {
                builder.Append(Convert.ToInt32(features[i].Id, CultureInfo.InvariantCulture));
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static FloatArray BuildFloatArray(IReadOnlyList<Feature> features, FieldDefinition field)
    {
        var builder = new FloatArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertFloat(GeoParquetQueryFormatter.GetAttributeValue(features[i], field.Name));
            if (value.HasValue)
            {
                builder.Append(value.Value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static DoubleArray BuildDoubleArray(IReadOnlyList<Feature> features, FieldDefinition field)
    {
        var builder = new DoubleArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertDouble(GeoParquetQueryFormatter.GetAttributeValue(features[i], field.Name));
            if (value.HasValue)
            {
                builder.Append(value.Value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static BooleanArray BuildBooleanArray(IReadOnlyList<Feature> features, FieldDefinition field)
    {
        var builder = new BooleanArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertBoolean(GeoParquetQueryFormatter.GetAttributeValue(features[i], field.Name));
            if (value.HasValue)
            {
                builder.Append(value.Value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static TimestampArray BuildTimestampArray(IReadOnlyList<Feature> features, FieldDefinition field)
    {
        var builder = new TimestampArray.Builder(new TimestampType(TimeUnit.Millisecond, "UTC"));
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertDateTimeOffset(GeoParquetQueryFormatter.GetAttributeValue(features[i], field.Name));
            if (value.HasValue)
            {
                builder.Append(value.Value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static Time64Array BuildTime64Array(IReadOnlyList<Feature> features, FieldDefinition field)
    {
        // Time64 stores microseconds since midnight as int64
        var builder = new Int64Array.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertTimeOnly(GeoParquetQueryFormatter.GetAttributeValue(features[i], field.Name));
            if (value.HasValue)
            {
                var microseconds = value.Value.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
                builder.Append(microseconds);
            }
            else
            {
                builder.AppendNull();
            }
        }

        var int64Array = builder.Build();

        // Wrap the raw int64 data in a Time64Array with proper type metadata
        return new Time64Array(
            new Time64Type(TimeUnit.Microsecond),
            int64Array.ValueBuffer,
            int64Array.NullBitmapBuffer,
            int64Array.Length,
            int64Array.NullCount,
            int64Array.Offset);
    }

    private static BinaryArray BuildBinaryArray(IReadOnlyList<Feature> features, FieldDefinition field)
    {
        var builder = new BinaryArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertBytes(GeoParquetQueryFormatter.GetAttributeValue(features[i], field.Name));
            if (value != null)
            {
                builder.Append(value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static StringArray BuildStringArray(IReadOnlyList<Feature> features, FieldDefinition field)
    {
        var builder = new StringArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = ConvertToStringValue(GeoParquetQueryFormatter.GetAttributeValue(features[i], field.Name));
            if (value != null)
            {
                builder.Append(value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static BinaryArray BuildGeometryArray(
        IReadOnlyList<Feature> features,
        int outputSrid,
        GeometryLimits geometryLimits,
        bool returnZ,
        bool returnM)
    {
        var builder = new BinaryArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var wkb = GeoParquetQueryFormatter.ProcessGeometry(
                features[i].Geometry, outputSrid, geometryLimits, returnZ, returnM);
            if (wkb != null)
            {
                builder.Append(wkb);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static string BuildExtensionMetadata(LayerDefinition layer, int srid, bool returnZ, bool isEmpty)
    {
        GeoParquetQueryFormatter.EnsureSupportedCloudNativeGeometrySrid(includeGeometry: true, srid, "GeoArrow");

        // Build JSON manually to avoid AOT issues (no source-generated context for
        // Dictionary<string, object?>). For EPSG:4326 the `crs` key is omitted,
        // which implies OGC:CRS84 per GeoParquet 1.1.0.
        // GeoParquet 1.1.0 §4.1: geometry_types lists types actually present —
        // an empty result must use [].
        var geometryTypesPart = isEmpty
            ? "[]"
            : $@"[""{GeoParquetQueryFormatter.MapGeometryTypeToGeoParquet(layer.GeometryType, returnZ)}""]";
        return $@"{{""geometry_types"":{geometryTypesPart},""edges"":""planar""}}";
    }

    private static Dictionary<string, string> BuildSchemaMetadata(
        LayerDefinition layer,
        bool includeGeometry,
        int srid,
        bool returnZ,
        bool isEmpty)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!includeGeometry)
        {
            return metadata;
        }

        GeoParquetQueryFormatter.EnsureSupportedCloudNativeGeometrySrid(includeGeometry: true, srid, "GeoArrow");

        // Build JSON manually to avoid AOT issues (matches GeoParquet pattern).
        // For EPSG:4326 the `crs` key is omitted, which implies OGC:CRS84.
        // GeoParquet 1.1.0 §4.1: geometry_types lists types actually present —
        // an empty result must use [].
        var geometryTypesPart = isEmpty
            ? "[]"
            : $@"[""{GeoParquetQueryFormatter.MapGeometryTypeToGeoParquet(layer.GeometryType, returnZ)}""]";
        var geoJson = $@"{{""version"":""1.1.0"",""primary_column"":""{GeometryColumnName}"",""columns"":{{""{GeometryColumnName}"":{{""encoding"":""WKB"",""geometry_types"":{geometryTypesPart}}}}}}}";

        metadata[GeoMetadataKey] = geoJson;
        return metadata;
    }

    // Type converters — duplicated from GeoParquetQueryFormatter because Arrow builder
    // API (Append/AppendNull) differs from Parquet's column-array model.

    private static long? TryConvertInt64(object? value)
    {
        return value switch
        {
            null => null,
            long longValue => longValue,
            int intValue => intValue,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            double doubleValue => Convert.ToInt64(doubleValue, CultureInfo.InvariantCulture),
            decimal decimalValue => Convert.ToInt64(decimalValue, CultureInfo.InvariantCulture),
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var jsonLong) => jsonLong,
            string stringValue when long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static int? TryConvertInt32(object? value)
    {
        return value switch
        {
            null => null,
            int intValue => intValue,
            long longValue => Convert.ToInt32(longValue, CultureInfo.InvariantCulture),
            short shortValue => shortValue,
            byte byteValue => byteValue,
            double doubleValue => Convert.ToInt32(doubleValue, CultureInfo.InvariantCulture),
            decimal decimalValue => Convert.ToInt32(decimalValue, CultureInfo.InvariantCulture),
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var jsonInt) => jsonInt,
            string stringValue when int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static float? TryConvertFloat(object? value)
    {
        return value switch
        {
            null => null,
            float floatValue => floatValue,
            double doubleValue => Convert.ToSingle(doubleValue, CultureInfo.InvariantCulture),
            decimal decimalValue => Convert.ToSingle(decimalValue, CultureInfo.InvariantCulture),
            int intValue => intValue,
            long longValue => longValue,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var jsonFloat) => jsonFloat,
            string stringValue when float.TryParse(stringValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static double? TryConvertDouble(object? value)
    {
        return value switch
        {
            null => null,
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            decimal decimalValue => Convert.ToDouble(decimalValue, CultureInfo.InvariantCulture),
            int intValue => intValue,
            long longValue => longValue,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var jsonDouble) => jsonDouble,
            string stringValue when double.TryParse(stringValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static bool? TryConvertBoolean(object? value)
    {
        return value switch
        {
            null => null,
            bool boolValue => boolValue,
            JsonElement element when element.ValueKind == JsonValueKind.True => true,
            JsonElement element when element.ValueKind == JsonValueKind.False => false,
            string stringValue when bool.TryParse(stringValue, out var parsedBool) => parsedBool,
            string stringValue when int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt) => parsedInt != 0,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            _ => null
        };
    }

    private static DateTimeOffset? TryConvertDateTimeOffset(object? value)
    {
        return value switch
        {
            null => null,
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            DateTime dateTime => dateTime.Kind switch
            {
                DateTimeKind.Utc => new DateTimeOffset(dateTime, TimeSpan.Zero),
                DateTimeKind.Local => new DateTimeOffset(dateTime.ToUniversalTime(), TimeSpan.Zero),
                _ => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc), TimeSpan.Zero)
            },
            DateOnly dateOnly => new DateTimeOffset(dateOnly.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            JsonElement element when element.ValueKind == JsonValueKind.String => TryConvertDateTimeOffset(element.GetString()),
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var milliseconds) =>
                DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
            string stringValue when DateTimeOffset.TryParse(
                stringValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
                out var parsedOffset) => parsedOffset.ToUniversalTime(),
            string stringValue when DateTime.TryParse(
                stringValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var parsedDateTime) => new DateTimeOffset(parsedDateTime, TimeSpan.Zero),
            long milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
            double milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(milliseconds, CultureInfo.InvariantCulture)),
            _ => null
        };
    }

    private static TimeOnly? TryConvertTimeOnly(object? value)
    {
        return value switch
        {
            null => null,
            TimeOnly timeOnly => timeOnly,
            TimeSpan timeSpan => TimeOnly.FromTimeSpan(timeSpan),
            DateTime dateTime => TimeOnly.FromDateTime(dateTime),
            DateTimeOffset dateTimeOffset => TimeOnly.FromDateTime(dateTimeOffset.UtcDateTime),
            JsonElement element when element.ValueKind == JsonValueKind.String => TryConvertTimeOnly(element.GetString()),
            string stringValue when TimeOnly.TryParse(stringValue, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedTime) => parsedTime,
            string stringValue when TimeSpan.TryParse(stringValue, CultureInfo.InvariantCulture, out var parsedSpan) => TimeOnly.FromTimeSpan(parsedSpan),
            _ => null
        };
    }

    private static byte[]? TryConvertBytes(object? value)
    {
        return value switch
        {
            null => null,
            byte[] bytes => bytes,
            JsonElement element when element.ValueKind == JsonValueKind.String => TryConvertBytes(element.GetString()),
            string stringValue when TryDecodeBase64(stringValue, out var decoded) => decoded,
            string stringValue => System.Text.Encoding.UTF8.GetBytes(stringValue),
            _ => null
        };
    }

    private static string? ConvertToStringValue(object? value)
    {
        return value switch
        {
            null => null,
            string stringValue => stringValue,
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            JsonElement element when element.ValueKind is JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static bool TryDecodeBase64(string? value, out byte[] decoded)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            decoded = [];
            return false;
        }

        try
        {
            decoded = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            decoded = [];
            return false;
        }
    }
}

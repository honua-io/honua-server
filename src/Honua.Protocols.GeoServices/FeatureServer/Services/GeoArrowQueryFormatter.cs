// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Services;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

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
    /// Formats query result as Arrow IPC Stream with GeoArrow metadata using Metadata v2 resource metadata.
    /// </summary>
    public static async Task<(byte[] response, string contentType)> FormatAsGeoArrowAsync(
        QueryResult<Feature> result,
        MetadataV2Resource resource,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        string[]? outFields = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var selectedFields = GeoParquetQueryFormatter.ResolveSelectedFields(resource, outFields);
        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        var includeGeometry = returnGeometry && HasGeometry(resource);
        var srid = outputSrid ?? resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        GeoParquetQueryFormatter.EnsureSupportedCloudNativeGeometrySrid(includeGeometry, srid, "GeoArrow");
        GeoParquetQueryFormatter.EnsureSupportedCloudNativeGeometryMeasures(includeGeometry, returnM, "GeoArrow");
        var features = result.Items;
        var runtimeFields = GeoParquetQueryFormatter.DetectRuntimeFields(features, resource);
        BinaryArray? geometryArray = null;
        string[] geometryTypes = [];
        if (includeGeometry)
        {
            (geometryArray, geometryTypes) = GeoParquetFeatureWriter.BuildGeoArrowGeometryArray(
                features, srid, returnZ, returnM, geometryLimits);
            if (resource.ReadGeometryType() == MetadataV2GeometryType.Mixed)
            {
                // An untyped PostGIS geometry column cannot truthfully advertise the concrete
                // types observed on one page; GeoParquet's unknown-type representation is [].
                geometryTypes = [];
            }
            else if (features.Length == 0
                && GeoParquetFeatureWriter.MapGeometryTypeToGeoParquet(
                    resource.ReadGeometryType(), returnZ) is { } knownType)
            {
                // An empty GeoArrow page has no values to inspect; preserve a known layer type
                // for the same schema self-description emitted by the existing empty-result path.
                geometryTypes = [knownType];
            }
        }

        var schema = BuildSchema(
            selectedFields, includeGeometry, resource, srid, returnZ, runtimeFields, outFields, geometryTypes);
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
            runtimeFields,
            geometryArray);

        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, schema, leaveOpen: true))
        {
            await writer.WriteRecordBatchAsync(recordBatch, cancellationToken).ConfigureAwait(false);
            await writer.WriteEndAsync(cancellationToken).ConfigureAwait(false);
        }

        return (stream.ToArray(), "application/vnd.apache.arrow.stream");
    }

    private static Apache.Arrow.Schema BuildSchema(
        List<MetadataV2Field> selectedFields,
        bool includeGeometry,
        MetadataV2Resource resource,
        int srid,
        bool returnZ,
        List<(string name, IArrowType type)> runtimeFields,
        string[]? outFields,
        IReadOnlyCollection<string> geometryTypes)
    {
        var fields = new List<Apache.Arrow.Field>(selectedFields.Count + (includeGeometry ? 1 : 0));

        foreach (var field in selectedFields)
        {
            fields.Add(CreateArrowField(field));
        }

        if (includeGeometry)
        {
            var fieldMetadata = new Dictionary<string, string>
            {
                [ArrowExtensionNameKey] = GeoArrowExtensionName
            };

            // GeoArrow 0.2: when no supported extension metadata key applies,
            // ARROW:extension:metadata must be omitted entirely (never an empty object).
            var extensionMetadata = BuildExtensionMetadata(srid);
            if (extensionMetadata is not null)
            {
                fieldMetadata[ArrowExtensionMetadataKey] = extensionMetadata;
            }

            var geometryField = new Apache.Arrow.Field(
                GeometryColumnName,
                BinaryType.Default,
                nullable: true,
                fieldMetadata);
            fields.Add(geometryField);
        }

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

        var schemaMetadata = BuildSchemaMetadata(resource, includeGeometry, srid, geometryTypes);
        return new Apache.Arrow.Schema(fields, schemaMetadata);
    }

    private static Apache.Arrow.Field CreateArrowField(MetadataV2Field field)
    {
        IArrowType arrowType = field.Type switch
        {
            MetadataV2FieldType.BigInteger => Int64Type.Default,
            MetadataV2FieldType.Integer => Int32Type.Default,
            MetadataV2FieldType.Float => FloatType.Default,
            MetadataV2FieldType.Double => DoubleType.Default,
            MetadataV2FieldType.Boolean => BooleanType.Default,
            MetadataV2FieldType.DateTime or MetadataV2FieldType.Date => new TimestampType(TimeUnit.Millisecond, "UTC"),
            MetadataV2FieldType.Time => new Time64Type(TimeUnit.Microsecond),
            MetadataV2FieldType.Binary => BinaryType.Default,
            _ => StringType.Default
        };

        return new Apache.Arrow.Field(field.Name, arrowType, field.Nullable);
    }

    private static RecordBatch BuildRecordBatch(
        IReadOnlyList<Feature> features,
        List<MetadataV2Field> selectedFields,
        string objectIdFieldName,
        bool includeGeometry,
        int outputSrid,
        GeometryLimits geometryLimits,
        bool returnZ,
        bool returnM,
        Apache.Arrow.Schema schema,
        IReadOnlyList<(string name, IArrowType type)> runtimeFields,
        BinaryArray? geometryArray)
    {
        var arrays = new List<IArrowArray>(schema.FieldsList.Count);

        foreach (var field in selectedFields)
        {
            var isObjectIdField = field.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase);
            arrays.Add(BuildAttributeArray(features, field, isObjectIdField));
        }

        if (includeGeometry)
        {
            arrays.Add(geometryArray ?? throw new InvalidOperationException(
                "GeoArrow geometry array was not built for a geometry-bearing response."));
        }

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
        MetadataV2Field field,
        bool isObjectIdField)
    {
        return field.Type switch
        {
            MetadataV2FieldType.BigInteger => BuildInt64Array(features, field.Name, isObjectIdField),
            MetadataV2FieldType.Integer => BuildInt32Array(features, field.Name, isObjectIdField),
            MetadataV2FieldType.Float => BuildFloatArray(features, field.Name),
            MetadataV2FieldType.Double => BuildDoubleArray(features, field.Name),
            MetadataV2FieldType.Boolean => BuildBooleanArray(features, field.Name),
            MetadataV2FieldType.DateTime or MetadataV2FieldType.Date => BuildTimestampArray(features, field.Name),
            MetadataV2FieldType.Time => BuildTime64Array(features, field.Name),
            MetadataV2FieldType.Binary => BuildBinaryArray(features, field.Name),
            _ => BuildStringArray(features, field.Name)
        };
    }

    private static Int64Array BuildInt64Array(IReadOnlyList<Feature> features, string fieldName, bool isObjectIdField)
    {
        var builder = new Int64Array.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName);
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

    private static Int32Array BuildInt32Array(IReadOnlyList<Feature> features, string fieldName, bool isObjectIdField)
    {
        var builder = new Int32Array.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName);
            var converted = TryConvertInt32(value);
            if (converted.HasValue)
            {
                builder.Append(converted.Value);
            }
            else if (isObjectIdField && features[i].Id >= int.MinValue && features[i].Id <= int.MaxValue)
            {
                // OID fallback. When an Integer-typed OID column holds a 64-bit value
                // outside Int32 range the column type is too narrow; degrade to null
                // rather than throwing OverflowException (consistent with GeoParquet).
                builder.Append((int)features[i].Id);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static FloatArray BuildFloatArray(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new FloatArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertFloat(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
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

    private static DoubleArray BuildDoubleArray(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new DoubleArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertDouble(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
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

    private static BooleanArray BuildBooleanArray(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new BooleanArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertBoolean(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
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

    private static TimestampArray BuildTimestampArray(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new TimestampArray.Builder(new TimestampType(TimeUnit.Millisecond, "UTC"));
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertDateTimeOffset(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
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

    private static Time64Array BuildTime64Array(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new Int64Array.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertTimeOnly(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
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

        return new Time64Array(
            new Time64Type(TimeUnit.Microsecond),
            int64Array.ValueBuffer,
            int64Array.NullBitmapBuffer,
            int64Array.Length,
            int64Array.NullCount,
            int64Array.Offset);
    }

    private static BinaryArray BuildBinaryArray(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new BinaryArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertBytes(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
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

    private static StringArray BuildStringArray(IReadOnlyList<Feature> features, string fieldName)
    {
        var builder = new StringArray.Builder();
        for (var i = 0; i < features.Count; i++)
        {
            var value = ConvertToStringValue(GeoParquetQueryFormatter.GetAttributeValue(features[i], fieldName));
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

    private static string? BuildExtensionMetadata(int srid)
    {
        GeoParquetQueryFormatter.EnsureSupportedCloudNativeGeometrySrid(includeGeometry: true, srid, "GeoArrow");

        // GeoArrow 0.2 (https://geoarrow.org/extension-types.html) supports only `crs`,
        // `crs_type`, and optional `edges` in ARROW:extension:metadata:
        // - `crs` is emitted as the authoritative PROJJSON for the output SRID. GeoArrow has
        //   no default CRS (omission means "unknown"), so the known output CRS must always be
        //   declared. Coordinates stay (x, y) / (longitude, latitude) per the GeoArrow
        //   axis-order rule regardless of the axis order encoded in the CRS definition.
        // - Planar/linear edges are declared by omitting `edges`; `"edges":"planar"` is not a
        //   valid value.
        // - `geometry_types` is not a GeoArrow key; it belongs only in the schema-level
        //   GeoParquet `geo` metadata (see BuildSchemaMetadata).
        if (!GeoParquetProjJsonCatalog.TryGetProjJson(srid, out var projJson))
        {
            // No supported extension metadata key applies; the caller omits
            // ARROW:extension:metadata entirely per the GeoArrow spec.
            return null;
        }

        return $@"{{""crs"":{projJson},""crs_type"":""projjson""}}";
    }

    private static Dictionary<string, string> BuildSchemaMetadata(
        MetadataV2Resource resource,
        bool includeGeometry,
        int srid,
        IReadOnlyCollection<string> geometryTypes)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!includeGeometry)
        {
            return metadata;
        }

        GeoParquetQueryFormatter.EnsureSupportedCloudNativeGeometrySrid(includeGeometry: true, srid, "GeoArrow");

        var geometryTypesPart = JsonSerializer.Serialize(
            geometryTypes.OrderBy(static type => type, StringComparer.Ordinal));
        var crsPart = GeoParquetProjJsonCatalog.TryGetProjJson(srid, out var projJson)
            ? $@",""crs"":{projJson}"
            : string.Empty;
        var geoJson = $@"{{""version"":""1.1.0"",""primary_column"":""{GeometryColumnName}"",""columns"":{{""{GeometryColumnName}"":{{""encoding"":""WKB"",""geometry_types"":{geometryTypesPart}{crsPart}}}}}}}";

        metadata[GeoMetadataKey] = geoJson;
        return metadata;
    }

    private static bool HasGeometry(MetadataV2Resource resource)
        => resource.ReadGeometryType() != MetadataV2GeometryType.None;

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
        // Out-of-range narrowing degrades to null (no value emitted) to match the
        // GeoParquet writer, which catches OverflowException and appends null. Range
        // tests avoid throwing on the hot path instead of relying on try/catch.
        return value switch
        {
            null => null,
            int intValue => intValue,
            long longValue when longValue >= int.MinValue && longValue <= int.MaxValue => (int)longValue,
            long => null,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            double doubleValue when doubleValue >= int.MinValue && doubleValue <= int.MaxValue => (int)Math.Round(doubleValue, MidpointRounding.ToEven),
            double => null,
            decimal decimalValue when decimalValue >= int.MinValue && decimalValue <= int.MaxValue => (int)Math.Round(decimalValue, MidpointRounding.ToEven),
            decimal => null,
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Parquet;
using Parquet.Schema;
using ParquetDataColumn = Parquet.Data.DataColumn;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Service for formatting query results as GeoParquet.
/// </summary>
internal sealed class GeoParquetQueryFormatter
{
    private const string GeometryColumnName = "geometry";
    private const string GeoMetadataKey = "geo";

    [ThreadStatic]
    private static WKBReader? _wkbReader;

    /// <summary>
    /// Formats query result as GeoParquet.
    /// </summary>
    public static async Task<(byte[] response, string contentType)> FormatAsGeoParquetAsync(
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
        var selectedFields = ResolveSelectedFields(layer, outFields);
        var objectIdFieldName = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;
        var schema = new ParquetSchema(BuildSchemaFields(selectedFields, returnGeometry && layer.HasGeometry));
        var features = result.Items.ToList();

        using var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream, cancellationToken: cancellationToken))
        {
            writer.CustomMetadata = BuildGeoParquetMetadata(layer, returnGeometry, outputSrid, returnZ);

            using (var rowGroupWriter = writer.CreateRowGroup())
            {
                foreach (var column in BuildColumns(
                    features,
                    selectedFields,
                    schema,
                    objectIdFieldName,
                    returnGeometry && layer.HasGeometry,
                    outputSrid ?? layer.SpatialReference.Wkid,
                    geometryLimits,
                    returnZ,
                    returnM))
                {
                    await rowGroupWriter.WriteColumnAsync(column, cancellationToken);
                }
            }
        }

        return (stream.ToArray(), "application/vnd.apache.parquet");
    }

    private static List<FieldDefinition> ResolveSelectedFields(LayerDefinition layer, string[]? outFields)
    {
        var objectIdFieldName = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;
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

        var selectedFields = layer.Fields
            .Where(field => !field.IsGeometry)
            .Where(field => includeAllFields || requestedFields!.Contains(field.Name))
            .ToList();

        if (!selectedFields.Any(field => field.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase)))
        {
            selectedFields.Insert(0, new FieldDefinition(objectIdFieldName, FieldType.BigInteger, Nullable: false));
        }

        return selectedFields;
    }

    private static Field[] BuildSchemaFields(List<FieldDefinition> selectedFields, bool includeGeometry)
    {
        var fields = new List<Field>(selectedFields.Count + (includeGeometry ? 1 : 0));

        foreach (var field in selectedFields)
        {
            fields.Add(CreateSchemaField(field));
        }

        if (includeGeometry)
        {
            fields.Add(new DataField<byte[]>(GeometryColumnName, true));
        }

        return fields.ToArray();
    }

    private static IEnumerable<ParquetDataColumn> BuildColumns(
        IReadOnlyList<Feature> features,
        List<FieldDefinition> selectedFields,
        ParquetSchema schema,
        string objectIdFieldName,
        bool includeGeometry,
        int outputSrid,
        GeometryLimits geometryLimits,
        bool returnZ,
        bool returnM)
    {
        foreach (var field in selectedFields)
        {
            yield return BuildAttributeColumn(
                features,
                schema.FindDataField(field.Name)!,
                field,
                field.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase));
        }

        if (includeGeometry)
        {
            var geometryField = schema.FindDataField(GeometryColumnName)!;
            var geometryValues = features
                .Select(feature => ProcessGeometry(feature.Geometry, outputSrid, geometryLimits, returnZ, returnM))
                .ToArray();

            yield return new ParquetDataColumn(geometryField, geometryValues);
        }
    }

    private static ParquetDataColumn BuildAttributeColumn(
        IReadOnlyList<Feature> features,
        DataField dataField,
        FieldDefinition field,
        bool isObjectIdField)
    {
        return field.Type switch
        {
            FieldType.BigInteger => new ParquetDataColumn(
                dataField,
                BuildInt64Values(features, field, isObjectIdField)),
            FieldType.Integer => new ParquetDataColumn(
                dataField,
                BuildInt32Values(features, field, isObjectIdField)),
            FieldType.Float => new ParquetDataColumn(
                dataField,
                BuildFloatValues(features, field)),
            FieldType.Double => new ParquetDataColumn(
                dataField,
                BuildDoubleValues(features, field)),
            FieldType.Boolean => new ParquetDataColumn(
                dataField,
                BuildBooleanValues(features, field)),
            FieldType.DateTime => new ParquetDataColumn(
                dataField,
                BuildDateTimeValues(features, field, dateOnly: false)),
            FieldType.Date => new ParquetDataColumn(
                dataField,
                BuildDateTimeValues(features, field, dateOnly: true)),
            FieldType.Time => new ParquetDataColumn(
                dataField,
                BuildTimeOnlyValues(features, field)),
            FieldType.Binary => new ParquetDataColumn(
                dataField,
                BuildBinaryValues(features, field)),
            _ => new ParquetDataColumn(
                dataField,
                BuildStringValues(features, field)),
        };
    }

    private static DataField CreateSchemaField(FieldDefinition field)
    {
        return field.Type switch
        {
            FieldType.BigInteger => new DataField(field.Name, field.Nullable ? typeof(long?) : typeof(long), field.Nullable),
            FieldType.Integer => new DataField(field.Name, field.Nullable ? typeof(int?) : typeof(int), field.Nullable),
            FieldType.Float => new DataField(field.Name, field.Nullable ? typeof(float?) : typeof(float), field.Nullable),
            FieldType.Double => new DataField(field.Name, field.Nullable ? typeof(double?) : typeof(double), field.Nullable),
            FieldType.Boolean => new DataField(field.Name, field.Nullable ? typeof(bool?) : typeof(bool), field.Nullable),
            FieldType.DateTime => new DateTimeDataField(
                field.Name,
                DateTimeFormat.DateAndTime,
                isAdjustedToUTC: true,
                DateTimeTimeUnit.Millis,
                field.Nullable),
            FieldType.Date => new DateTimeDataField(
                field.Name,
                DateTimeFormat.Date,
                isAdjustedToUTC: true,
                unit: null,
                field.Nullable),
            FieldType.Time => new TimeOnlyDataField(field.Name, TimeSpanFormat.MilliSeconds, field.Nullable),
            FieldType.Binary => new DataField<byte[]>(field.Name, field.Nullable),
            _ => new DataField<string>(field.Name, field.Nullable),
        };
    }

    private static Array BuildInt64Values(IReadOnlyList<Feature> features, FieldDefinition field, bool isObjectIdField)
    {
        if (isObjectIdField)
        {
            var values = new long[features.Count];
            for (var i = 0; i < features.Count; i++)
            {
                values[i] = GetInt64Value(features[i], field, isObjectIdField);
            }

            return values;
        }

        var nullableValues = new long?[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            nullableValues[i] = TryConvertInt64(GetAttributeValue(features[i], field.Name));
        }

        return nullableValues;
    }

    private static Array BuildInt32Values(IReadOnlyList<Feature> features, FieldDefinition field, bool isObjectIdField)
    {
        if (isObjectIdField)
        {
            var objectIdValues = new int[features.Count];
            for (var i = 0; i < features.Count; i++)
            {
                objectIdValues[i] = Convert.ToInt32(GetInt64Value(features[i], field, isObjectIdField), CultureInfo.InvariantCulture);
            }

            return objectIdValues;
        }

        var values = new int?[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            values[i] = TryConvertInt32(GetAttributeValue(features[i], field.Name));
        }

        return values;
    }

    private static float?[] BuildFloatValues(IReadOnlyList<Feature> features, FieldDefinition field)
    {
        var values = new float?[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            values[i] = TryConvertFloat(GetAttributeValue(features[i], field.Name));
        }

        return values;
    }

    private static double?[] BuildDoubleValues(IReadOnlyList<Feature> features, FieldDefinition field)
    {
        var values = new double?[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            values[i] = TryConvertDouble(GetAttributeValue(features[i], field.Name));
        }

        return values;
    }

    private static bool?[] BuildBooleanValues(IReadOnlyList<Feature> features, FieldDefinition field)
    {
        var values = new bool?[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            values[i] = TryConvertBoolean(GetAttributeValue(features[i], field.Name));
        }

        return values;
    }

    private static DateTime?[] BuildDateTimeValues(IReadOnlyList<Feature> features, FieldDefinition field, bool dateOnly)
    {
        var values = new DateTime?[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            var value = TryConvertDateTime(GetAttributeValue(features[i], field.Name));
            values[i] = dateOnly && value.HasValue
                ? DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc)
                : value;
        }

        return values;
    }

    private static TimeOnly?[] BuildTimeOnlyValues(IReadOnlyList<Feature> features, FieldDefinition field)
    {
        var values = new TimeOnly?[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            values[i] = TryConvertTimeOnly(GetAttributeValue(features[i], field.Name));
        }

        return values;
    }

    private static byte[]?[] BuildBinaryValues(IReadOnlyList<Feature> features, FieldDefinition field)
    {
        var values = new byte[]?[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            values[i] = TryConvertBytes(GetAttributeValue(features[i], field.Name));
        }

        return values;
    }

    private static string?[] BuildStringValues(IReadOnlyList<Feature> features, FieldDefinition field)
    {
        var values = new string?[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            values[i] = ConvertToStringValue(GetAttributeValue(features[i], field.Name));
        }

        return values;
    }

    private static long GetInt64Value(Feature feature, FieldDefinition field, bool isObjectIdField)
    {
        var value = GetAttributeValue(feature, field.Name);
        return TryConvertInt64(value) ?? (isObjectIdField ? feature.Id : 0L);
    }

    private static object? GetAttributeValue(Feature feature, string fieldName)
    {
        return feature.Attributes.TryGetValue(fieldName, out var value) ? value : null;
    }

    private static byte[]? ProcessGeometry(
        byte[]? geometryBytes,
        int outputSrid,
        GeometryLimits geometryLimits,
        bool returnZ,
        bool returnM)
    {
        if (geometryBytes == null || geometryBytes.Length == 0)
        {
            return null;
        }

        var geometry = GetWkbReader().Read(geometryBytes);
        if (geometry == null)
        {
            return null;
        }

        geometry.SRID = outputSrid;
        geometry = GeometryOutputProcessor.ApplyLimits(geometry, geometryLimits);
        if (geometry == null)
        {
            return null;
        }

        if (!returnZ || !returnM)
        {
            geometry = GeometryOutputProcessor.ApplyDimensionFilter(geometry, returnZ, returnM);
        }

        var hasZ = GeometryHasZ(geometry);
        var hasM = GeometryHasM(geometry);
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: hasZ, emitM: hasM);
        return writer.Write(geometry);
    }

    private static Dictionary<string, string> BuildGeoParquetMetadata(
        LayerDefinition layer,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ)
    {
        if (!returnGeometry || !layer.HasGeometry)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var srid = outputSrid ?? layer.SpatialReference.Wkid;
        var geometryType = MapGeometryTypeToGeoParquet(layer.GeometryType, returnZ);

        var geoMetadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = "1.1.0",
            ["primary_column"] = GeometryColumnName,
            ["columns"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [GeometryColumnName] = BuildGeometryColumnMetadata(layer, srid, geometryType)
            }
        };

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GeoMetadataKey] = JsonSerializer.Serialize(geoMetadata)
        };
    }

    private static Dictionary<string, object?> BuildGeometryColumnMetadata(
        LayerDefinition layer,
        int srid,
        string geometryType)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["encoding"] = "WKB",
            ["geometry_types"] = new[] { geometryType },
            ["crs"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "name",
                ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = $"EPSG:{srid}"
                }
            }
        };

        if (layer.Extent.HasValue && srid == layer.SpatialReference.Wkid)
        {
            metadata["bbox"] = new[]
            {
                layer.Extent.Value.MinX,
                layer.Extent.Value.MinY,
                layer.Extent.Value.MaxX,
                layer.Extent.Value.MaxY
            };
        }

        return metadata;
    }

    private static string MapGeometryTypeToGeoParquet(Honua.Core.Features.Catalog.Domain.GeometryType geometryType, bool returnZ)
    {
        var baseType = geometryType switch
        {
            Honua.Core.Features.Catalog.Domain.GeometryType.Point => "Point",
            Honua.Core.Features.Catalog.Domain.GeometryType.LineString => "LineString",
            Honua.Core.Features.Catalog.Domain.GeometryType.Polygon => "Polygon",
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiPoint => "MultiPoint",
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiLineString => "MultiLineString",
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiPolygon => "MultiPolygon",
            Honua.Core.Features.Catalog.Domain.GeometryType.GeometryCollection => "GeometryCollection",
            _ => "Geometry"
        };

        return returnZ ? $"{baseType} Z" : baseType;
    }

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

    private static DateTime? TryConvertDateTime(object? value)
    {
        return value switch
        {
            null => null,
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
            DateTime dateTime => dateTime.Kind switch
            {
                DateTimeKind.Utc => dateTime,
                DateTimeKind.Local => dateTime.ToUniversalTime(),
                _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            },
            DateOnly dateOnly => DateTime.SpecifyKind(dateOnly.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            JsonElement element when element.ValueKind == JsonValueKind.String => TryConvertDateTime(element.GetString()),
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var milliseconds) =>
                DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime,
            string stringValue when DateTimeOffset.TryParse(
                stringValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
                out var parsedOffset) => parsedOffset.UtcDateTime,
            string stringValue when DateTime.TryParse(
                stringValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var parsedDateTime) => parsedDateTime,
            long milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime,
            double milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(milliseconds, CultureInfo.InvariantCulture)).UtcDateTime,
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

    private static bool GeometryHasZ(Geometry geometry)
        => geometry.Coordinates.Any(coordinate => !double.IsNaN(coordinate.Z));

    private static bool GeometryHasM(Geometry geometry)
        => geometry.Coordinates.Any(coordinate => !double.IsNaN(coordinate.M));

    private static WKBReader GetWkbReader()
    {
        _wkbReader ??= new WKBReader();
        return _wkbReader;
    }
}

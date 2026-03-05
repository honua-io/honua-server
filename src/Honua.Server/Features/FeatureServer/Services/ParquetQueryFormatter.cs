// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ParquetDataColumn = Parquet.Data.DataColumn;
using GeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;

namespace Honua.Server.Features.FeatureServer.Services;

internal sealed class ParquetQueryFormatter
{
    public (byte[] Response, string ContentType) FormatAsParquet(
        QueryResult<Feature> result,
        LayerDefinition layer,
        bool returnGeometry,
        int? outputSrid,
        string[]? outFields)
    {
        var objectIdFieldName = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;
        var geometryFieldName = layer.Fields.FirstOrDefault(static field => field.IsGeometry)?.Name ?? "geometry";
        var includeGeometry = returnGeometry && layer.HasGeometry;

        var attributeFieldNames = ResolveAttributeFieldNames(layer, outFields, objectIdFieldName, geometryFieldName);
        var objectIdValues = new long[result.Items.Length];
        var attributeValues = new Dictionary<string, string?[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var fieldName in attributeFieldNames)
        {
            attributeValues[fieldName] = new string?[result.Items.Length];
        }

        byte[]?[]? geometryValues = includeGeometry ? new byte[]?[result.Items.Length] : null;

        for (var i = 0; i < result.Items.Length; i++)
        {
            var feature = result.Items[i];
            objectIdValues[i] = ResolveObjectId(feature, objectIdFieldName);

            foreach (var fieldName in attributeFieldNames)
            {
                feature.Attributes.TryGetValue(fieldName, out var rawValue);
                attributeValues[fieldName][i] = ConvertAttributeToString(rawValue);
            }

            if (geometryValues != null)
            {
                geometryValues[i] = feature.Geometry;
            }
        }

        var objectIdField = new DataField<long>(objectIdFieldName);
        var attributeFields = attributeFieldNames
            .Select(static fieldName => new DataField<string>(fieldName, true))
            .ToArray();
        var geometryField = includeGeometry ? new DataField<byte[]>(geometryFieldName, true) : null;

        var schemaFields = new List<Field>(1 + attributeFields.Length + (geometryField == null ? 0 : 1))
        {
            objectIdField
        };
        schemaFields.AddRange(attributeFields);
        if (geometryField != null)
        {
            schemaFields.Add(geometryField);
        }

        var schema = new ParquetSchema(schemaFields.ToArray());
        using var stream = new MemoryStream();

        using (var writer = ParquetWriter.CreateAsync(schema, stream).GetAwaiter().GetResult())
        {
            writer.CustomMetadata = BuildCustomMetadata(includeGeometry, geometryFieldName, layer.GeometryType, outputSrid);

            using var rowGroupWriter = writer.CreateRowGroup();
            rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, objectIdValues)).GetAwaiter().GetResult();

            for (var i = 0; i < attributeFields.Length; i++)
            {
                rowGroupWriter.WriteColumnAsync(
                    new ParquetDataColumn(attributeFields[i], attributeValues[attributeFieldNames[i]])).GetAwaiter().GetResult();
            }

            if (geometryField != null && geometryValues != null)
            {
                rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometryField, geometryValues)).GetAwaiter().GetResult();
            }
        }

        return (stream.ToArray(), "application/vnd.apache.parquet");
    }

    private static Dictionary<string, string> BuildCustomMetadata(
        bool includeGeometry,
        string geometryFieldName,
        GeometryType geometryType,
        int? outputSrid)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["honua_format"] = "query_parquet"
        };

        if (!includeGeometry)
        {
            return metadata;
        }

        var srid = outputSrid ?? SpatialReference.WGS84.Wkid;
        metadata["honua_srid"] = srid.ToString(CultureInfo.InvariantCulture);

        var geoMetadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = "1.1.0",
            ["primary_column"] = geometryFieldName,
            ["columns"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [geometryFieldName] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["encoding"] = "WKB",
                    ["geometry_types"] = MapGeometryTypes(geometryType),
                    ["crs"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["authority"] = "EPSG",
                            ["code"] = srid
                        }
                    }
                }
            }
        };

        metadata["geo"] = JsonSerializer.Serialize(geoMetadata);
        return metadata;
    }

    private static string[] ResolveAttributeFieldNames(
        LayerDefinition layer,
        string[]? outFields,
        string objectIdFieldName,
        string geometryFieldName)
    {
        var includeAllFields = outFields == null || outFields.Length == 0 ||
            (outFields.Length == 1 && outFields[0].Equals("*", StringComparison.Ordinal));

        if (includeAllFields)
        {
            return [.. layer.Fields
                .Where(static field => !field.IsGeometry)
                .Select(static field => field.Name)
                .Where(fieldName =>
                    !fieldName.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase) &&
                    !fieldName.Equals(geometryFieldName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        return [.. outFields!
            .Where(fieldName =>
                !fieldName.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase) &&
                !fieldName.Equals(geometryFieldName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static long ResolveObjectId(Feature feature, string objectIdFieldName)
    {
        if (!feature.Attributes.TryGetValue(objectIdFieldName, out var objectIdValue) || objectIdValue == null)
        {
            return feature.Id;
        }

        return objectIdValue switch
        {
            long longValue => longValue,
            int intValue => intValue,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            ulong ulongValue when ulongValue <= long.MaxValue => (long)ulongValue,
            uint uintValue => uintValue,
            ushort ushortValue => ushortValue,
            sbyte sbyteValue => sbyteValue,
            decimal decimalValue => Convert.ToInt64(decimalValue, CultureInfo.InvariantCulture),
            double doubleValue => Convert.ToInt64(doubleValue, CultureInfo.InvariantCulture),
            float floatValue => Convert.ToInt64(floatValue, CultureInfo.InvariantCulture),
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Number && jsonElement.TryGetInt64(out var parsed) => parsed,
            _ => feature.Id
        };
    }

    private static string? ConvertAttributeToString(object? value)
    {
        return value switch
        {
            null => null,
            string text => text,
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            JsonElement jsonElement => ConvertJsonElementToString(jsonElement),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static string? ConvertJsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            JsonValueKind.Object => element.GetRawText(),
            JsonValueKind.Array => element.GetRawText(),
            _ => element.GetRawText()
        };
    }

    private static string[] MapGeometryTypes(GeometryType geometryType)
    {
        return geometryType switch
        {
            GeometryType.Point => ["Point"],
            GeometryType.MultiPoint => ["MultiPoint"],
            GeometryType.LineString => ["LineString"],
            GeometryType.MultiLineString => ["MultiLineString"],
            GeometryType.Polygon => ["Polygon"],
            GeometryType.MultiPolygon => ["MultiPolygon"],
            GeometryType.GeometryCollection => ["GeometryCollection"],
            _ => ["Geometry"]
        };
    }
}

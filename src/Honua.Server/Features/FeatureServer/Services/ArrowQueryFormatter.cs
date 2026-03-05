// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using GeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;

namespace Honua.Server.Features.FeatureServer.Services;

internal sealed class ArrowQueryFormatter
{
    public (byte[] Response, string ContentType) FormatAsArrow(
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

        var objectIdBuilder = new Int64Array.Builder();
        var attributeBuilders = new Dictionary<string, StringArray.Builder>(StringComparer.OrdinalIgnoreCase);
        foreach (var fieldName in attributeFieldNames)
        {
            attributeBuilders[fieldName] = new StringArray.Builder();
        }

        BinaryArray.Builder? geometryBuilder = includeGeometry ? new BinaryArray.Builder() : null;

        foreach (var feature in result.Items)
        {
            objectIdBuilder.Append(ResolveObjectId(feature, objectIdFieldName));

            foreach (var fieldName in attributeFieldNames)
            {
                feature.Attributes.TryGetValue(fieldName, out var rawValue);
                var value = ConvertAttributeToString(rawValue);
                if (value == null)
                {
                    attributeBuilders[fieldName].AppendNull();
                }
                else
                {
                    attributeBuilders[fieldName].Append(value, Encoding.UTF8);
                }
            }

            if (geometryBuilder != null)
            {
                if (feature.Geometry is { Length: > 0 } geometry)
                {
                    geometryBuilder.Append(geometry.AsSpan());
                }
                else
                {
                    geometryBuilder.AppendNull();
                }
            }
        }

        var fields = new List<Field>(1 + attributeFieldNames.Length + (includeGeometry ? 1 : 0))
        {
            new(objectIdFieldName, Int64Type.Default, false)
        };

        var arrays = new List<IArrowArray>(1 + attributeFieldNames.Length + (includeGeometry ? 1 : 0))
        {
            objectIdBuilder.Build()
        };

        foreach (var fieldName in attributeFieldNames)
        {
            fields.Add(new Field(fieldName, StringType.Default, true));
            arrays.Add(attributeBuilders[fieldName].Build());
        }

        if (includeGeometry && geometryBuilder != null)
        {
            var geometryMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ARROW:extension:name"] = "geoarrow.wkb",
                ["ARROW:extension:metadata"] = BuildGeometryExtensionMetadata(layer.GeometryType, outputSrid)
            };

            fields.Add(new Field(geometryFieldName, BinaryType.Default, true, geometryMetadata));
            arrays.Add(geometryBuilder.Build());
        }

        var schema = new Schema(fields, BuildSchemaMetadata(includeGeometry, geometryFieldName, layer.GeometryType, outputSrid));

        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, schema, leaveOpen: true))
        using (var recordBatch = new RecordBatch(schema, arrays, result.Items.Length))
        {
            writer.WriteRecordBatch(recordBatch);
            writer.WriteEnd();
        }

        return (stream.ToArray(), "application/vnd.apache.arrow.stream");
    }

    private static IReadOnlyDictionary<string, string> BuildSchemaMetadata(
        bool includeGeometry,
        string geometryFieldName,
        GeometryType geometryType,
        int? outputSrid)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["honua_format"] = "query_arrow"
        };

        if (!includeGeometry)
        {
            return metadata;
        }

        var srid = outputSrid ?? SpatialReference.WGS84.Wkid;
        metadata["honua_srid"] = srid.ToString(CultureInfo.InvariantCulture);
        metadata["geo"] = BuildGeoMetadata(geometryFieldName, geometryType, srid);
        return metadata;
    }

    private static string BuildGeoMetadata(string geometryFieldName, GeometryType geometryType, int srid)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
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

        return JsonSerializer.Serialize(metadata);
    }

    private static string BuildGeometryExtensionMetadata(GeometryType geometryType, int? outputSrid)
    {
        var srid = outputSrid ?? SpatialReference.WGS84.Wkid;
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["crs"] = $"EPSG:{srid}",
            ["geometry_types"] = MapGeometryTypes(geometryType)
        };

        return JsonSerializer.Serialize(metadata);
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

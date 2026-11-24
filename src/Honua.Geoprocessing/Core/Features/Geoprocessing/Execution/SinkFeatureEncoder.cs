// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal static partial class SinkFeatureEncoder
{
    public const string BatchIdPropertyKey = "__pipeline_batch_id";

    public static string Identifier(string value)
    {
        if (!IdentifierRegex().IsMatch(value))
        {
            throw new TransformInputException(
                $"identifier '{value}' is invalid; identifiers must match ^[A-Za-z_][A-Za-z0-9_]*$ " +
                "(they cannot be parameterized in DDL/DML).");
        }

        return value;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*\\z")]
    private static partial Regex IdentifierRegex();

    public static string BuildAttributesJson(IFeature feature, string batchId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(BatchIdPropertyKey, batchId);

            if (feature.Attributes is { } table)
            {
                var names = table.GetNames();
                var values = table.GetValues();
                for (var i = 0; i < names.Length; i++)
                {
                    WriteAttribute(writer, names[i], values[i]);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static void WriteAttribute(
        Utf8JsonWriter writer,
        string name,
        object? value,
        bool writeNonFiniteAsNull = false)
    {
        writer.WritePropertyName(name);
        switch (value)
        {
            case null or DBNull:
                writer.WriteNullValue();
                break;
            case JsonElement jsonElement:
                jsonElement.WriteTo(writer);
                break;
            case string stringValue:
                writer.WriteStringValue(stringValue);
                break;
            case bool boolValue:
                writer.WriteBooleanValue(boolValue);
                break;
            case byte byteValue:
                writer.WriteNumberValue(byteValue);
                break;
            case sbyte sbyteValue:
                writer.WriteNumberValue(sbyteValue);
                break;
            case short shortValue:
                writer.WriteNumberValue(shortValue);
                break;
            case ushort ushortValue:
                writer.WriteNumberValue(ushortValue);
                break;
            case int intValue:
                writer.WriteNumberValue(intValue);
                break;
            case uint uintValue:
                writer.WriteNumberValue(uintValue);
                break;
            case long longValue:
                writer.WriteNumberValue(longValue);
                break;
            case ulong ulongValue:
                writer.WriteNumberValue(ulongValue);
                break;
            case float floatValue when float.IsFinite(floatValue):
                writer.WriteNumberValue(floatValue);
                break;
            case float when writeNonFiniteAsNull:
                // NaN / +/-Infinity are not valid JSON numbers; write null to preserve numeric column typing.
                writer.WriteNullValue();
                break;
            case double doubleValue when double.IsFinite(doubleValue):
                writer.WriteNumberValue(doubleValue);
                break;
            case double when writeNonFiniteAsNull:
                writer.WriteNullValue();
                break;
            case decimal decimalValue:
                writer.WriteNumberValue(decimalValue);
                break;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime);
                break;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset);
                break;
            case Guid guid:
                writer.WriteStringValue(guid);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}

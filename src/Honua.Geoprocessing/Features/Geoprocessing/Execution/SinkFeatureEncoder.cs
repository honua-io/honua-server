// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Shared encoding helper for PostGIS-family sinks: turns an NTS <see cref="IFeature"/>'s
/// attributes into the canonical JSONB object every sink row carries, tagged with the
/// reserved <see cref="BatchIdPropertyKey"/> for soft-delete rollback. Centralising it
/// keeps the row wire-format identical across the external-PostGIS sink and the catalog
/// honua-layer sink instead of duplicating the per-type JSON projection.
/// </summary>
internal static class SinkFeatureEncoder
{
    /// <summary>Reserved attribute key tagging every row with its run batch id.</summary>
    public const string BatchIdPropertyKey = "__pipeline_batch_id";

    /// <summary>
    /// Builds the attributes JSON object for a feature, writing the reserved batch-id key
    /// first followed by the feature's own attributes.
    /// </summary>
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

    private static void WriteAttribute(Utf8JsonWriter writer, string name, object? value)
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
            case double doubleValue when double.IsFinite(doubleValue):
                writer.WriteNumberValue(doubleValue);
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

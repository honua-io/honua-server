// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static class GeoServicesFieldConventions
{
    // String fields never emit a null or non-positive length, because arcpy maps that to 0 and then rejects every insert as field length exceeded.
    internal const int DefaultStringFieldLength = 256;

    internal static int ResolveStringFieldLength(int? declaredLength)
        => declaredLength is int length and > 0 ? length : DefaultStringFieldLength;

    // sqlType is only the portable Esri member, using sqlTypeOther where the logical type is ambiguous, so the layer resource and the query response describe the same field.
    internal static string MapSqlType(MetadataV2FieldType type)
        => type switch
        {
            MetadataV2FieldType.String => "sqlTypeNVarchar",
            MetadataV2FieldType.Integer => "sqlTypeInteger",
            MetadataV2FieldType.BigInteger => "sqlTypeOther",
            MetadataV2FieldType.Double => "sqlTypeFloat",
            MetadataV2FieldType.Float => "sqlTypeFloat",
            MetadataV2FieldType.Boolean => "sqlTypeOther",
            MetadataV2FieldType.DateTime => "sqlTypeOther",
            MetadataV2FieldType.Date => "sqlTypeOther",
            MetadataV2FieldType.Time => "sqlTypeNVarchar",
            MetadataV2FieldType.Json => "sqlTypeNVarchar",
            MetadataV2FieldType.Binary => "sqlTypeOther",
            MetadataV2FieldType.Uuid => "sqlTypeOther",
            MetadataV2FieldType.Geometry => "sqlTypeOther",
            MetadataV2FieldType.Geography => "sqlTypeOther",
            _ => "sqlTypeOther"
        };

    internal static string MapFieldType(MetadataV2FieldType type)
        => type switch
        {
            MetadataV2FieldType.String => "esriFieldTypeString",
            MetadataV2FieldType.Integer => "esriFieldTypeInteger",
            MetadataV2FieldType.BigInteger => "esriFieldTypeBigInteger",
            MetadataV2FieldType.Double => "esriFieldTypeDouble",
            MetadataV2FieldType.Float => "esriFieldTypeSingle",
            MetadataV2FieldType.Boolean => "esriFieldTypeSmallInteger",
            MetadataV2FieldType.DateTime => "esriFieldTypeDate",
            MetadataV2FieldType.Date => "esriFieldTypeDateOnly",
            MetadataV2FieldType.Time => "esriFieldTypeString",
            MetadataV2FieldType.Json => "esriFieldTypeString",
            MetadataV2FieldType.Binary => "esriFieldTypeBlob",
            MetadataV2FieldType.Uuid => "esriFieldTypeGUID",
            MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography => "esriFieldTypeGeometry",
            _ => "esriFieldTypeString"
        };

    internal static long ToEpochMilliseconds(DateTime value)
        => new DateTimeOffset(DateTime.SpecifyKind(
            value,
            value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : value.Kind)).ToUnixTimeMilliseconds();

    internal static long ToEpochMilliseconds(DateOnly value)
        => new DateTimeOffset(value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    internal static bool TryConvertToEpochMilliseconds(object value, out long epochMilliseconds)
    {
        switch (value)
        {
            case DateTimeOffset dto:
                epochMilliseconds = dto.ToUnixTimeMilliseconds();
                return true;
            case DateTime dt:
                epochMilliseconds = ToEpochMilliseconds(dt);
                return true;
            case DateOnly dateOnly:
                epochMilliseconds = ToEpochMilliseconds(dateOnly);
                return true;
            case long l:
                epochMilliseconds = l;
                return true;
            case int i:
                epochMilliseconds = i;
                return true;
            case JsonElement element:
                return TryConvertJsonElementToEpochMilliseconds(element, out epochMilliseconds);
            case string text:
                return TryParseDateStringToEpochMilliseconds(text, out epochMilliseconds);
        }

        epochMilliseconds = 0;
        return false;
    }

    internal static void CoerceDateAttributes(
        IDictionary<string, object?> attributes,
        IReadOnlyCollection<string> dateFieldNames)
    {
        // codeql[cs/linq/missed-where]: writes converted epoch values back into the same attribute dictionary
        foreach (var fieldName in dateFieldNames)
        {
            if (attributes.TryGetValue(fieldName, out var value) &&
                value is not null &&
                TryConvertToEpochMilliseconds(value, out var epochMilliseconds))
            {
                attributes[fieldName] = epochMilliseconds;
            }
        }
    }

    private static bool TryConvertJsonElementToEpochMilliseconds(JsonElement element, out long epochMilliseconds)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetInt64(out var epoch):
                epochMilliseconds = epoch;
                return true;
            case JsonValueKind.String:
                return TryParseDateStringToEpochMilliseconds(element.GetString(), out epochMilliseconds);
        }

        epochMilliseconds = 0;
        return false;
    }

    private static bool TryParseDateStringToEpochMilliseconds(string? text, out long epochMilliseconds)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Already an epoch-ms integer encoded as a string.
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            {
                epochMilliseconds = epoch;
                return true;
            }

            if (DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsedOffset))
            {
                epochMilliseconds = parsedOffset.ToUnixTimeMilliseconds();
                return true;
            }

            // Date-only string (no time component) -> midnight UTC.
            if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                epochMilliseconds = ToEpochMilliseconds(parsedDate);
                return true;
            }
        }

        epochMilliseconds = 0;
        return false;
    }

    internal static Dictionary<string, MetadataV2FieldType> ResolveTemporalFieldTypes(MetadataV2Resource resource)
        => resource.SchemaFields
            .Where(static field => field.Type is MetadataV2FieldType.DateTime or MetadataV2FieldType.Date)
            .ToDictionary(static field => field.Name, static field => field.Type, StringComparer.OrdinalIgnoreCase);

    internal static bool TryConvertTemporalValue(object value, MetadataV2FieldType fieldType, out object? converted)
    {
        converted = null;
        if (fieldType == MetadataV2FieldType.DateTime && TryConvertToEpochMilliseconds(value, out var epoch))
        {
            converted = epoch;
            return true;
        }
        if (fieldType == MetadataV2FieldType.Date && TryConvertCalendarDate(value, out var date))
        {
            converted = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }
        return false;
    }

    internal static object? NormalizeFieldDefault(MetadataV2Field field)
    {
        if (field.DefaultValue is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (TryConvertTemporalValue(value, field.Type, out var converted))
        {
            return converted;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer :
                value.TryGetDouble(out var number) ? number : value.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => value.Clone()
        };
    }

    private static bool TryConvertCalendarDate(object value, out DateOnly date)
    {
        switch (value)
        {
            case DateOnly calendar:
                date = calendar;
                return true;
            case DateTime timestamp:
                date = DateOnly.FromDateTime(timestamp);
                return true;
            case DateTimeOffset timestamp:
                date = DateOnly.FromDateTime(timestamp.DateTime);
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.String:
                return TryConvertCalendarDate(element.GetString()!, out date);
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var epoch):
                return TryConvertCalendarDate(epoch, out date);
            case string text:
                if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    return true;
                // Existing canonical Date values may have crossed a DateTime/JSON
                // cache boundary. Retain the represented calendar day, not the UTC day.
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTimestamp))
                {
                    date = DateOnly.FromDateTime(parsedTimestamp.DateTime);
                    return true;
                }
                break;
            case int epoch:
                return TryConvertCalendarDate((long)epoch, out date);
            case long epoch:
                // Compatibility for stored values written under the former epoch
                // representation of canonical Date fields.
                try
                {
                    date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime);
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    break;
                }
        }
        date = default;
        return false;
    }

    internal static void CoerceTemporalAttributes(
        IDictionary<string, object?> attributes,
        IReadOnlyDictionary<string, MetadataV2FieldType> temporalFieldTypes)
    {
        // codeql[cs/linq/missed-where]: writes converted temporal values back into the same attribute dictionary
        foreach (var field in temporalFieldTypes)
        {
            if (attributes.TryGetValue(field.Key, out var dateValue) &&
                dateValue is not null &&
                TryConvertTemporalValue(dateValue, field.Value, out var converted))
            {
                attributes[field.Key] = converted;
            }
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Shared Esri GeoServices field-metadata conventions used by the FeatureServer and
/// MapServer field-info projections.
/// </summary>
internal static class GeoServicesFieldConventions
{
    /// <summary>
    /// Esri-conventional default <c>length</c> emitted for <c>esriFieldTypeString</c>
    /// fields whose backing column declares no explicit varchar length.
    /// </summary>
    /// <remarks>
    /// A real Esri FeatureServer always reports a positive <c>length</c> for string
    /// fields. arcpy maps a null/absent length to 0 and then rejects every insert/update
    /// with "Field length exceeded", so the f=json metadata must never emit a null or
    /// non-positive length for a string field.
    /// </remarks>
    internal const int DefaultStringFieldLength = 256;

    /// <summary>
    /// Resolves the <c>length</c> to report for a string field. Uses the column's
    /// declared varchar length when it is a positive value; otherwise falls back to the
    /// Esri-conventional <see cref="DefaultStringFieldLength"/>.
    /// </summary>
    /// <param name="declaredLength">The declared varchar length, when known.</param>
    internal static int ResolveStringFieldLength(int? declaredLength)
        => declaredLength is int length and > 0 ? length : DefaultStringFieldLength;

    /// <summary>
    /// Converts a <see cref="DateTime"/> to epoch milliseconds (UTC). Values with an
    /// unspecified kind are treated as UTC.
    /// </summary>
    internal static long ToEpochMilliseconds(DateTime value)
        => new DateTimeOffset(DateTime.SpecifyKind(
            value,
            value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : value.Kind)).ToUnixTimeMilliseconds();

    /// <summary>
    /// Converts a <see cref="DateOnly"/> to epoch milliseconds at midnight UTC.
    /// </summary>
    internal static long ToEpochMilliseconds(DateOnly value)
        => new DateTimeOffset(value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>
    /// Coerces a value known to belong to an <c>esriFieldTypeDate</c> field into an
    /// epoch-millisecond (UTC) integer. Handles <see cref="DateTime"/>,
    /// <see cref="DateTimeOffset"/>, <see cref="DateOnly"/>, already-numeric epoch values,
    /// <see cref="JsonElement"/>, and ISO-8601 / date-only strings. Date-only values are
    /// treated as midnight UTC. Already-numeric epoch values pass through unchanged so the
    /// conversion is idempotent.
    /// </summary>
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

    /// <summary>
    /// Temporal field types used to distinguish timestamp epochs from calendar dates.
    /// </summary>
    internal static Dictionary<string, MetadataV2FieldType> ResolveTemporalFieldTypes(MetadataV2Resource resource)
        => resource.SchemaFields
            .Where(static field => field.Type is MetadataV2FieldType.DateTime or MetadataV2FieldType.Date)
            .ToDictionary(static field => field.Name, static field => field.Type, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes a declared temporal value to its GeoServices wire representation.
    /// Calendar dates retain their calendar day without time-zone conversion.
    /// </summary>
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

    /// <summary>
    /// Coerces temporal attributes consistently across query, identify, related records
    /// and replication. Timestamps use epoch milliseconds; calendar dates use ISO dates.
    /// Null and unconvertible values are left unchanged.
    /// </summary>
    internal static void CoerceTemporalAttributes(
        IDictionary<string, object?> attributes,
        IReadOnlyDictionary<string, MetadataV2FieldType> temporalFieldTypes)
    {
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

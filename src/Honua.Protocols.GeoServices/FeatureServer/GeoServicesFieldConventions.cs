// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

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
}

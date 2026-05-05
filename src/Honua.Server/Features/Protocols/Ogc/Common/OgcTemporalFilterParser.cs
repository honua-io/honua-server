// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Protocols.Ogc.Common;

internal static class OgcTemporalFilterParser
{
    public static bool TryParse(
        string? datetime,
        LayerDefinition layer,
        out TemporalFilter? temporalFilter,
        out string? errorMessage)
    {
        temporalFilter = null;

        if (!TryParseRange(datetime, out var start, out var end, out errorMessage))
        {
            return false;
        }

        if (start is null && end is null)
        {
            return true;
        }

        if (!TryResolveTemporalFields(layer, out var startField, out var endField))
        {
            errorMessage = "No temporal field is available for filtering.";
            return false;
        }
        var resolvedField = startField!;

        temporalFilter = new TemporalFilter
        {
            PropertyName = resolvedField.Name,
            PropertyType = resolvedField.Type == FieldType.Date ? TemporalPropertyType.Date : TemporalPropertyType.DateTime,
            EndPropertyName = endField?.Name,
            Start = start,
            End = end
        };

        return true;
    }

    /// <summary>
    /// Parses an OGC API datetime parameter (RFC 3339 instant or interval) into a
    /// (start, end) pair without requiring a layer. Supported forms: instant,
    /// <c>start/end</c>, <c>../end</c>, <c>start/..</c>. For an instant T both
    /// <paramref name="start"/> and <paramref name="end"/> are set to T.
    /// </summary>
    public static bool TryParseRange(
        string? datetime,
        out DateTimeOffset? start,
        out DateTimeOffset? end,
        out string? errorMessage)
    {
        start = null;
        end = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(datetime))
        {
            return true;
        }

        var parts = datetime.Split('/', StringSplitOptions.TrimEntries);

        if (parts.Length == 1)
        {
            if (!TryParseDateTimeOffset(parts[0], out var instant))
            {
                errorMessage = "Invalid datetime parameter.";
                return false;
            }

            start = instant;
            end = instant;
            return true;
        }

        if (parts.Length == 2)
        {
            if (!string.IsNullOrWhiteSpace(parts[0]) && parts[0] != "..")
            {
                if (!TryParseDateTimeOffset(parts[0], out var parsedStart))
                {
                    errorMessage = "Invalid datetime parameter.";
                    return false;
                }

                start = parsedStart;
            }

            if (!string.IsNullOrWhiteSpace(parts[1]) && parts[1] != "..")
            {
                if (!TryParseDateTimeOffset(parts[1], out var parsedEnd))
                {
                    errorMessage = "Invalid datetime parameter.";
                    return false;
                }

                end = parsedEnd;
            }

            if (start is null && end is null)
            {
                errorMessage = "Invalid datetime parameter.";
                return false;
            }

            if (start is { } s && end is { } e && s > e)
            {
                errorMessage = "Invalid datetime parameter.";
                return false;
            }

            return true;
        }

        errorMessage = "Invalid datetime parameter.";
        return false;
    }

    private static bool TryParseDateTimeOffset(string value, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);

    private static bool TryResolveTemporalFields(
        LayerDefinition layer,
        out FieldDefinition? startField,
        out FieldDefinition? endField)
    {
        startField = null;
        endField = null;

        var timeInfo = layer.Metadata?.TimeInfo;
        var startFieldName = timeInfo?.StartTimeField;
        if (!string.IsNullOrWhiteSpace(startFieldName))
        {
            startField = layer.AttributeFields.FirstOrDefault(field =>
                field.Name.Equals(startFieldName, StringComparison.OrdinalIgnoreCase) &&
                field.Type is FieldType.DateTime or FieldType.Date);
            if (startField is null)
            {
                return false;
            }

            // Optional EndTimeField, when configured, is propagated so the
            // shared filter pipeline applies interval-intersection semantics
            // (see TemporalFilter.EndPropertyName); ignore the configured end
            // field if it does not resolve to a Date/DateTime attribute. Fields
            // must share the same temporal type so the COALESCE in the SQL
            // builder produces a homogeneous expression.
            var endFieldName = timeInfo?.EndTimeField;
            if (!string.IsNullOrWhiteSpace(endFieldName))
            {
                var startFieldType = startField.Type;
                var candidate = layer.AttributeFields.FirstOrDefault(field =>
                    field.Name.Equals(endFieldName, StringComparison.OrdinalIgnoreCase) &&
                    field.Type == startFieldType);
                if (candidate is not null)
                {
                    endField = candidate;
                }
            }

            return true;
        }

        startField = layer.AttributeFields.FirstOrDefault(field =>
            field.Type is FieldType.DateTime or FieldType.Date);
        return startField != null;
    }
}

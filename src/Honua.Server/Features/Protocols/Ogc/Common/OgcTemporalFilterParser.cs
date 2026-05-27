// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

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
    /// Metadata v2 overload of
    /// <see cref="TryParse(string, LayerDefinition, out TemporalFilter?, out string?)"/>.
    /// Resolves the start/end temporal fields from the V2 resource via
    /// <see cref="MetadataV2SpatialExtensions.ReadTemporalFields"/> and the resource schema
    /// fields, defaulting to the first DateTime/Date schema field when the resource declares
    /// no explicit temporal-field configuration.
    /// </summary>
    public static bool TryParse(
        string? datetime,
        MetadataV2Resource resource,
        out TemporalFilter? temporalFilter,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(resource);
        temporalFilter = null;

        if (!TryParseRange(datetime, out var start, out var end, out errorMessage))
        {
            return false;
        }

        if (start is null && end is null)
        {
            return true;
        }

        if (!TryResolveTemporalFieldsV2(
                resource,
                out var startName,
                out var startType,
                out var endName))
        {
            errorMessage = "No temporal field is available for filtering.";
            return false;
        }

        temporalFilter = new TemporalFilter
        {
            PropertyName = startName!,
            PropertyType = startType == FieldType.Date ? TemporalPropertyType.Date : TemporalPropertyType.DateTime,
            EndPropertyName = endName,
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

    /// <summary>
    /// Metadata v2 equivalent of <see cref="TryResolveTemporalFields(LayerDefinition, out FieldDefinition?, out FieldDefinition?)"/>.
    /// Reads the configured start/end fields from
    /// <see cref="MetadataV2SpatialExtensions.ReadTemporalFields"/>, validates each against
    /// the resource schema, and falls back to the first DateTime/Date schema field when no
    /// explicit configuration is present.
    /// </summary>
    private static bool TryResolveTemporalFieldsV2(
        MetadataV2Resource resource,
        out string? startFieldName,
        out FieldType startFieldType,
        out string? endFieldName)
    {
        startFieldName = null;
        startFieldType = FieldType.DateTime;
        endFieldName = null;

        var temporal = resource.ReadTemporalFields();
        var configuredStartName = temporal.StartTimeField;
        if (!string.IsNullOrWhiteSpace(configuredStartName))
        {
            if (!TryResolveSchemaTemporalType(resource, configuredStartName, out var startType))
            {
                return false;
            }

            startFieldName = configuredStartName;
            startFieldType = startType;

            // Optional EndTimeField, when configured, is propagated so the
            // shared filter pipeline applies interval-intersection semantics.
            // Ignore the configured end field if it does not resolve to the
            // same temporal type as the start field (matching v1 semantics so
            // the COALESCE in the SQL builder produces a homogeneous
            // expression).
            var configuredEndName = temporal.EndTimeField;
            if (!string.IsNullOrWhiteSpace(configuredEndName) &&
                TryResolveSchemaTemporalType(resource, configuredEndName, out var endType) &&
                endType == startType)
            {
                endFieldName = configuredEndName;
            }

            return true;
        }

        foreach (var field in resource.SchemaFields)
        {
            if (TryResolveTemporalTypeLabel(field.Type, out var inferredType))
            {
                startFieldName = field.Name;
                startFieldType = inferredType;
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveSchemaTemporalType(
        MetadataV2Resource resource,
        string fieldName,
        out FieldType type)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (!string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryResolveTemporalTypeLabel(field.Type, out type))
            {
                return true;
            }
        }

        type = FieldType.DateTime;
        return false;
    }

    private static bool TryResolveTemporalTypeLabel(MetadataV2FieldType typeLabel, out FieldType type)
    {
        switch (typeLabel)
        {
            case MetadataV2FieldType.DateTime:
                type = FieldType.DateTime;
                return true;
            case MetadataV2FieldType.Date:
                type = FieldType.Date;
                return true;
            default:
                type = FieldType.DateTime;
                return false;
        }
    }
}

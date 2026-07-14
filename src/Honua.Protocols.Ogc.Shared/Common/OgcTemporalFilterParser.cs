// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.ZarrParser;

namespace Honua.Protocols.Ogc.Common;

internal static class OgcTemporalFilterParser
{
    /// <summary>
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
                out var propertyType,
                out var endName))
        {
            errorMessage = "No temporal field is available for filtering.";
            return false;
        }

        temporalFilter = new TemporalFilter
        {
            PropertyName = startName!,
            PropertyType = propertyType,
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
    /// <paramref name="start"/> and <paramref name="end"/> are set to T. Delegates to
    /// the neutral <see cref="Iso8601TemporalIntervalParser"/> in <c>Honua.Core</c> so
    /// the OGC adapters and the coverage/datacube tile path share one implementation.
    /// </summary>
    public static bool TryParseRange(
        string? datetime,
        out DateTimeOffset? start,
        out DateTimeOffset? end,
        out string? errorMessage)
        => Iso8601TemporalIntervalParser.TryParseRange(datetime, out start, out end, out errorMessage);

    private static bool TryResolveTemporalFieldsV2(
        MetadataV2Resource resource,
        out string? startFieldName,
        out TemporalPropertyType propertyType,
        out string? endFieldName)
    {
        startFieldName = null;
        propertyType = TemporalPropertyType.DateTime;
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
            propertyType = startType;

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

        // judgment call: TryResolveTemporalTypeLabel both filters (skips non-temporal field
        // types) and binds the extracted `inferredType` used below; a .Where() would have to
        // re-resolve the type, so the guard-clause form here is clearer.
        foreach (var field in resource.SchemaFields)
        {
            if (TryResolveTemporalTypeLabel(field.Type, out var inferredType))
            {
                startFieldName = field.Name;
                propertyType = inferredType;
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveSchemaTemporalType(
        MetadataV2Resource resource,
        string fieldName,
        out TemporalPropertyType type)
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

        type = TemporalPropertyType.DateTime;
        return false;
    }

    private static bool TryResolveTemporalTypeLabel(MetadataV2FieldType typeLabel, out TemporalPropertyType type)
    {
        switch (typeLabel)
        {
            case MetadataV2FieldType.DateTime:
                type = TemporalPropertyType.DateTime;
                return true;
            case MetadataV2FieldType.Date:
                type = TemporalPropertyType.Date;
                return true;
            default:
                type = TemporalPropertyType.DateTime;
                return false;
        }
    }
}

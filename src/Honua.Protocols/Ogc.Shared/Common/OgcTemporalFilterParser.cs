// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.ZarrParser;

namespace Honua.Protocols.Ogc.Common;

internal static class OgcTemporalFilterParser
{
    public static bool TryParse(
        string? datetime,
        MetadataV2Object resource,
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

    public static bool TryParseRange(
        string? datetime,
        out DateTimeOffset? start,
        out DateTimeOffset? end,
        out string? errorMessage)
        => Iso8601TemporalIntervalParser.TryParseRange(datetime, out start, out end, out errorMessage);

    private static bool TryResolveTemporalFieldsV2(
        MetadataV2Object resource,
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
            // Filter on the schema's spelling: attribute keys and storage columns are case-sensitive.
            if (!TryFindSchemaTemporalField(resource, configuredStartName, out var startField))
            {
                return false;
            }

            startFieldName = startField.Name;
            propertyType = startField.Type;

            // Optional EndTimeField, when configured, is propagated so the
            // shared filter pipeline applies interval-intersection semantics.
            // Ignore the configured end field if it does not resolve to the
            // same temporal type as the start field (matching v1 semantics so
            // the COALESCE in the SQL builder produces a homogeneous
            // expression).
            var configuredEndName = temporal.EndTimeField;
            if (!string.IsNullOrWhiteSpace(configuredEndName) &&
                TryFindSchemaTemporalField(resource, configuredEndName, out var endField) &&
                endField.Type == startField.Type)
            {
                endFieldName = endField.Name;
            }

            return true;
        }

        var inferred = resource.SchemaFields
            .Select(field => (
                FieldName: field.Name,
                Type: TryResolveTemporalTypeLabel(field.Type, out var type)
                    ? (TemporalPropertyType?)type
                    : null))
            .FirstOrDefault(candidate => candidate.Type.HasValue);
        if (inferred.Type.HasValue)
        {
            startFieldName = inferred.FieldName;
            propertyType = inferred.Type.Value;
            return true;
        }

        return false;
    }

    private static bool TryFindSchemaTemporalField(
        MetadataV2Object resource,
        string fieldName,
        out (string Name, TemporalPropertyType Type) match)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (!string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryResolveTemporalTypeLabel(field.Type, out var type))
            {
                match = (field.Name, type);
                return true;
            }
        }

        match = (fieldName, TemporalPropertyType.DateTime);
        return false;
    }

    private static bool TryResolveTemporalTypeLabel(MetadataV2FieldType typeLabel, out TemporalPropertyType type)
    {
        switch (typeLabel)
        {
            case MetadataV2FieldType.DateTime:
            case MetadataV2FieldType.TimestampOffset:
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

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.Ogc.Common;

internal static class OgcQueryablesUtilities
{
    public static bool IsSimpleQueryableField(MetadataV2Field field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Type is MetadataV2FieldType.String
            or MetadataV2FieldType.Integer
            or MetadataV2FieldType.BigInteger
            or MetadataV2FieldType.Double
            or MetadataV2FieldType.Float
            or MetadataV2FieldType.Boolean
            or MetadataV2FieldType.DateTime
            or MetadataV2FieldType.Date
            or MetadataV2FieldType.Time
            or MetadataV2FieldType.Uuid;
    }

    public static async Task<TemporalExtent?> BuildTemporalExtentAsync(
        MetadataV2Object resource,
        int layerIndex,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(featureReader);

        if (!TryResolveTemporalFieldsV2(resource, out var startName, out var startType, out var endName, out var endType))
        {
            return null;
        }

        TemporalExtentResult? startExtent;
        try
        {
            startExtent = await featureReader.GetTemporalExtentAsync(
                layerIndex, startName!, startType, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return null;
        }

        TemporalExtentResult? endExtent = null;
        if (!string.IsNullOrWhiteSpace(endName) &&
            !string.Equals(endName, startName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                endExtent = await featureReader.GetTemporalExtentAsync(
                    layerIndex, endName!, endType, cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        var hasExtent = startExtent != null || endExtent != null;
        if (!hasExtent)
        {
            return null;
        }

        var min = startExtent?.Start;
        DateTimeOffset? max = string.IsNullOrWhiteSpace(endName)
            ? startExtent?.End
            : endExtent?.End ?? endExtent?.Start ?? startExtent?.End;

        return new TemporalExtent
        {
            Interval = ImmutableArray.Create(ImmutableArray.Create(
                FormatTemporalValue(min),
                FormatTemporalValue(max)))
        };
    }

    public static bool TryResolveTemporalFieldsV2(
        MetadataV2Object resource,
        out string? startFieldName,
        out TemporalPropertyType startPropertyType,
        out string? endFieldName,
        out TemporalPropertyType endPropertyType)
    {
        ArgumentNullException.ThrowIfNull(resource);
        startFieldName = null;
        endFieldName = null;
        startPropertyType = TemporalPropertyType.DateTime;
        endPropertyType = TemporalPropertyType.DateTime;

        var fields = resource.ReadTemporalFields();
        if (string.IsNullOrWhiteSpace(fields.StartTimeField))
        {
            return false;
        }

        if (!TryResolveSchemaTemporalType(resource, fields.StartTimeField, out var startType))
        {
            return false;
        }

        startFieldName = fields.StartTimeField;
        startPropertyType = startType;

        if (string.IsNullOrWhiteSpace(fields.EndTimeField))
        {
            return true;
        }

        if (!TryResolveSchemaTemporalType(resource, fields.EndTimeField, out var endType))
        {
            // End field is configured but not in schema: fail the whole resolution.
            startFieldName = null;
            startPropertyType = TemporalPropertyType.DateTime;
            return false;
        }

        endFieldName = fields.EndTimeField;
        endPropertyType = endType;
        return true;
    }

    private static bool TryResolveSchemaTemporalType(
        MetadataV2Object resource,
        string fieldName,
        out TemporalPropertyType type)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (!string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (field.Type)
            {
                case MetadataV2FieldType.DateTime:
                    type = TemporalPropertyType.DateTime;
                    return true;
                case MetadataV2FieldType.Date:
                    type = TemporalPropertyType.Date;
                    return true;
            }
        }

        type = TemporalPropertyType.DateTime;
        return false;
    }

    private static string? FormatTemporalValue(DateTimeOffset? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        var utc = value.Value.ToUniversalTime();
        var format = utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? "yyyy-MM-ddTHH:mm:ss'Z'"
            : "yyyy-MM-ddTHH:mm:ss.fffffff'Z'";
        return utc.ToString(format, CultureInfo.InvariantCulture);
    }
}

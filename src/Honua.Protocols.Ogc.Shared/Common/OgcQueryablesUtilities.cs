// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.Ogc.Common;

/// <summary>
/// Neutral OGC queryables/temporal-extent helpers shared by the OGC API Features
/// adapter and the STAC Filter Extension. Both surfaces derive a queryable property
/// set from a Metadata V2 resource schema and build a collection temporal extent from
/// the configured temporal fields, so the behavior lives in the shared OGC foundation
/// rather than being reached across protocol adapters.
/// </summary>
internal static class OgcQueryablesUtilities
{
    /// <summary>
    /// Determines whether a Metadata V2 field is queryable for simple parameter filtering.
    /// </summary>
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

    /// <summary>
    /// Builds a temporal extent from the Metadata V2 temporal declaration and feature store values.
    /// </summary>
    /// <param name="resource">V2 resource carrying the temporal field declaration.</param>
    /// <param name="layerIndex">Service-local integer layer id used by the feature reader.</param>
    /// <param name="featureReader">Feature reader providing the extent probe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<TemporalExtent?> BuildTemporalExtentAsync(
        MetadataV2Resource resource,
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
        DateTimeOffset? max;
        if (string.IsNullOrWhiteSpace(endName))
        {
            max = startExtent?.End;
        }
        else
        {
            max = endExtent?.End ?? endExtent?.Start ?? startExtent?.End;
        }

        return new TemporalExtent
        {
            Interval = ImmutableArray.Create(ImmutableArray.Create(
                FormatTemporalValue(min),
                FormatTemporalValue(max)))
        };
    }

    /// <summary>
    /// Resolves the start/end temporal field names + canonical types from a V2 resource.
    /// Reads <see cref="MetadataV2Resource.Temporal"/> for the configured field names and
    /// looks up the field type in <see cref="MetadataV2Resource.SchemaFields"/>. Returns
    /// false when no start field is declared, the start field is not present in the schema,
    /// or the schema field type is not a recognized temporal type.
    /// </summary>
    public static bool TryResolveTemporalFieldsV2(
        MetadataV2Resource resource,
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

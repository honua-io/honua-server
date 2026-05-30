// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Infrastructure.Helpers;

internal static class TemporalExtentHelpers
{
    /// <summary>
    /// Canonical UTC formatter for OGC temporal capability values
    /// (<c>TIME</c> dimensions, <c>&lt;Default&gt;</c>, <c>&lt;Current&gt;</c>,
    /// extent literals). Uses second precision when the timestamp falls on a
    /// whole second boundary and 7-digit fractional precision otherwise so
    /// sub-second extents survive the round-trip from capabilities through to
    /// the temporal filter pipeline. Postgres compares timestamps inclusively
    /// at full precision; advertising a truncated max would exclude the row
    /// containing the layer's actual maximum.
    /// </summary>
    public static string FormatOgcTemporalValue(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var format = utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? "yyyy-MM-ddTHH:mm:ss'Z'"
            : "yyyy-MM-ddTHH:mm:ss.fffffff'Z'";
        return utc.ToString(format, CultureInfo.InvariantCulture);
    }


    internal readonly record struct MetadataV2TemporalRange(
        string StartFieldName,
        string? EndFieldName,
        DateTimeOffset? Min,
        DateTimeOffset? Max,
        bool HasExtent);

    internal readonly record struct MetadataV2TemporalFieldSelection(
        string StartFieldName,
        string? EndFieldName);

    /// <summary>
    /// Returns true when the resource declares a configured <c>startTimeField</c>
    /// via <see cref="MetadataV2Resource.Temporal"/> AND that field resolves to a
    /// date/datetime entry in <see cref="MetadataV2Resource.SchemaFields"/>.
    /// </summary>
    public static bool HasOptInTemporalFields(MetadataV2Resource resource)
        => TryResolveOptInTemporalFieldsV2(resource, out _);

    /// <summary>
    /// Resolves the configured opt-in temporal field names from the resource's
    /// temporal extension; returns false (selection=default) when the resource has no
    /// <c>startTimeField</c> or it doesn't match a schema field of date/datetime type.
    /// </summary>
    public static bool TryResolveOptInTemporalFieldsV2(
        MetadataV2Resource resource,
        out MetadataV2TemporalFieldSelection selection)
    {
        ArgumentNullException.ThrowIfNull(resource);
        selection = default;

        var fields = resource.ReadTemporalFields();
        if (string.IsNullOrWhiteSpace(fields.StartTimeField))
        {
            return false;
        }

        if (!TryFindSchemaTemporalField(resource, fields.StartTimeField, out var startName))
        {
            return false;
        }

        string? endName = null;
        if (!string.IsNullOrWhiteSpace(fields.EndTimeField))
        {
            if (!TryFindSchemaTemporalField(resource, fields.EndTimeField, out endName))
            {
                return false;
            }
        }

        selection = new MetadataV2TemporalFieldSelection(startName!, endName);
        return true;
    }

    private static bool TryFindSchemaTemporalField(MetadataV2Resource resource, string fieldName, out string? resolvedName)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                if (field.Type is MetadataV2FieldType.Date
                    or MetadataV2FieldType.DateTime
                    or MetadataV2FieldType.Time)
                {
                    resolvedName = field.Name;
                    return true;
                }
            }
        }
        resolvedName = null;
        return false;
    }

    /// <summary>
    /// Reads the start/end field names from <see cref="MetadataV2Resource.Temporal"/> and probes
    /// the feature store for the time range. Returns a typed range (start/end fields and the
    /// observed extent) so capability handlers can advertise the layer's time dimension.
    /// </summary>
    /// <param name="resource">V2 resource carrying the temporal extension.</param>
    /// <param name="layerIndex">The integer layer id used by the feature store (publication.LayerIndex).</param>
    /// <param name="featureReader">Feature store reader.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<MetadataV2TemporalRange?> TryResolveTemporalRangeV2Async(
        MetadataV2Resource resource,
        int layerIndex,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(featureReader);

        var fields = resource.ReadTemporalFields();
        if (string.IsNullOrWhiteSpace(fields.StartTimeField))
        {
            return null;
        }

        var startPropertyType = ResolveSchemaPropertyType(resource, fields.StartTimeField);
        TemporalExtentResult? startExtent;
        try
        {
            startExtent = await featureReader.GetTemporalExtentAsync(
                layerIndex,
                fields.StartTimeField,
                startPropertyType,
                cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return null;
        }

        TemporalExtentResult? endExtent = null;
        if (!string.IsNullOrWhiteSpace(fields.EndTimeField) &&
            !string.Equals(fields.EndTimeField, fields.StartTimeField, StringComparison.OrdinalIgnoreCase))
        {
            var endPropertyType = ResolveSchemaPropertyType(resource, fields.EndTimeField);
            try
            {
                endExtent = await featureReader.GetTemporalExtentAsync(
                    layerIndex,
                    fields.EndTimeField,
                    endPropertyType,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        var min = startExtent?.Start;
        DateTimeOffset? max;
        if (string.IsNullOrWhiteSpace(fields.EndTimeField))
        {
            max = startExtent?.End;
        }
        else
        {
            max = endExtent?.End ?? endExtent?.Start ?? startExtent?.End;
        }

        var hasExtent = startExtent != null || endExtent != null;
        return new MetadataV2TemporalRange(
            fields.StartTimeField!,
            fields.EndTimeField,
            min,
            max,
            hasExtent);
    }

    private static TemporalPropertyType ResolveSchemaPropertyType(MetadataV2Resource resource, string fieldName)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return field.Type switch
                {
                    MetadataV2FieldType.Date => TemporalPropertyType.Date,
                    _ => TemporalPropertyType.DateTime,
                };
            }
        }
        return TemporalPropertyType.DateTime;
    }

}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Infrastructure.Helpers;

internal static class TemporalExtentHelpers
{
    // A truncated maximum would exclude the row that holds the layer's actual maximum, because the database compares timestamps at full precision.
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

    public static bool HasOptInTemporalFields(MetadataV2Object resource)
        => TryResolveOptInTemporalFieldsV2(resource, out _);

    public static bool TryResolveOptInTemporalFieldsV2(
        MetadataV2Object resource,
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
        if (!string.IsNullOrWhiteSpace(fields.EndTimeField) &&
            !TryFindSchemaTemporalField(resource, fields.EndTimeField, out endName))
        {
            return false;
        }

        selection = new MetadataV2TemporalFieldSelection(startName!, endName);
        return true;
    }

    private static bool TryFindSchemaTemporalField(MetadataV2Object resource, string fieldName, out string? resolvedName)
    {
        foreach (var field in resource.SchemaFields.Where(field =>
            string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase) &&
            field.Type is MetadataV2FieldType.Date or MetadataV2FieldType.DateTime or MetadataV2FieldType.Time))
        {
            resolvedName = field.Name;
            return true;
        }

        resolvedName = null;
        return false;
    }

    public static async Task<MetadataV2TemporalRange?> TryResolveTemporalRangeV2Async(
        MetadataV2Object resource,
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
        DateTimeOffset? max = string.IsNullOrWhiteSpace(fields.EndTimeField)
            ? startExtent?.End
            : endExtent?.End ?? endExtent?.Start ?? startExtent?.End;

        var hasExtent = startExtent != null || endExtent != null;
        return new MetadataV2TemporalRange(
            fields.StartTimeField!,
            fields.EndTimeField,
            min,
            max,
            hasExtent);
    }

    private static TemporalPropertyType ResolveSchemaPropertyType(MetadataV2Object resource, string fieldName)
    {
        foreach (var field in resource.SchemaFields.Where(field =>
            string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase)))
        {
            return field.Type switch
            {
                MetadataV2FieldType.Date => TemporalPropertyType.Date,
                _ => TemporalPropertyType.DateTime,
            };
        }
        return TemporalPropertyType.DateTime;
    }

}

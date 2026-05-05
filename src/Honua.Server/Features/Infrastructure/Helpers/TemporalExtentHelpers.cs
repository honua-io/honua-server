// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Infrastructure.Helpers;

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


    internal readonly record struct TemporalRange(
        FieldDefinition StartField,
        FieldDefinition? EndField,
        DateTimeOffset? Min,
        DateTimeOffset? Max,
        bool HasExtent);

    internal readonly record struct TemporalFieldSelection(
        FieldDefinition StartField,
        FieldDefinition? EndField);

    private enum TemporalFieldResolutionFailure
    {
        None,
        StartFieldNotDefined,
        StartFieldNotFound,
        EndFieldNotFound,
        NoTemporalField,
        MismatchedTypes
    }

    public static TemporalFieldSelection ResolveTemporalFieldsOrThrow(LayerDefinition layer)
    {
        if (!TryResolveTemporalFields(
                layer,
                allowFallbackWhenMissingStart: true,
                out var startField,
                out var endField,
                out var failure,
                out var fieldName))
        {
            var message = failure switch
            {
                TemporalFieldResolutionFailure.StartFieldNotFound =>
                    $"Temporal field '{fieldName}' is not defined on layer '{layer.Name}'.",
                TemporalFieldResolutionFailure.EndFieldNotFound =>
                    $"Temporal field '{fieldName}' is not defined on layer '{layer.Name}'.",
                TemporalFieldResolutionFailure.NoTemporalField or TemporalFieldResolutionFailure.StartFieldNotDefined =>
                    $"No temporal field found in layer '{layer.Name}' for temporal query.",
                TemporalFieldResolutionFailure.MismatchedTypes =>
                    "Start and end time fields must use the same temporal type.",
                _ => "Invalid temporal field configuration."
            };

            throw new ArgumentException(message);
        }

        return new TemporalFieldSelection(startField!, endField);
    }

    public static async Task<TemporalRange?> TryResolveTemporalRangeAsync(
        LayerDefinition layer,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        if (!TryResolveTemporalFields(
                layer,
                allowFallbackWhenMissingStart: false,
                out var startField,
                out var endField,
                out _,
                out _))
        {
            return null;
        }

        TemporalExtentResult? startExtent = await featureReader.GetTemporalExtentAsync(
            layer.Id,
            startField!.Name,
            startField.Type,
            cancellationToken).ConfigureAwait(false);

        TemporalExtentResult? endExtent = null;
        if (endField != null && !endField.Name.Equals(startField.Name, StringComparison.OrdinalIgnoreCase))
        {
            endExtent = await featureReader.GetTemporalExtentAsync(
                layer.Id,
                endField.Name,
                endField.Type,
                cancellationToken).ConfigureAwait(false);
        }

        var min = startExtent?.Start;
        var max = endField == null
            ? startExtent?.End
            : endExtent?.End ?? endExtent?.Start;

        return new TemporalRange(startField, endField, min, max, startExtent != null);
    }

    private static bool TryResolveTemporalFields(
        LayerDefinition layer,
        bool allowFallbackWhenMissingStart,
        out FieldDefinition? startField,
        out FieldDefinition? endField,
        out TemporalFieldResolutionFailure failure,
        out string? fieldName)
    {
        startField = null;
        endField = null;
        failure = TemporalFieldResolutionFailure.None;
        fieldName = null;

        var timeInfo = layer.Metadata?.TimeInfo;
        if (timeInfo != null)
        {
            if (string.IsNullOrWhiteSpace(timeInfo.StartTimeField))
            {
                if (!allowFallbackWhenMissingStart)
                {
                    failure = TemporalFieldResolutionFailure.StartFieldNotDefined;
                    return false;
                }
            }
            else
            {
                startField = FindTemporalField(layer, timeInfo.StartTimeField);
                if (startField == null)
                {
                    failure = TemporalFieldResolutionFailure.StartFieldNotFound;
                    fieldName = timeInfo.StartTimeField;
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(timeInfo.EndTimeField))
            {
                endField = FindTemporalField(layer, timeInfo.EndTimeField);
                if (endField == null)
                {
                    failure = TemporalFieldResolutionFailure.EndFieldNotFound;
                    fieldName = timeInfo.EndTimeField;
                    return false;
                }
            }
        }
        else
        {
            startField = layer.AttributeFields.FirstOrDefault(field => field.Type is FieldType.DateTime or FieldType.Date);
        }

        if (startField == null)
        {
            failure = TemporalFieldResolutionFailure.NoTemporalField;
            return false;
        }

        if (endField != null && endField.Type != startField.Type)
        {
            failure = TemporalFieldResolutionFailure.MismatchedTypes;
            return false;
        }

        return true;
    }

    private static FieldDefinition? FindTemporalField(LayerDefinition layer, string fieldName)
    {
        return layer.AttributeFields.FirstOrDefault(field =>
            field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase) &&
            field.Type is FieldType.DateTime or FieldType.Date);
    }
}

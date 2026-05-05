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

    /// <summary>
    /// Returns true when the layer is opt-in time-aware AND the configured
    /// <c>TimeInfo.StartTimeField</c> (and optional <c>EndTimeField</c>) actually
    /// resolve to <c>Date</c>/<c>DateTime</c> attributes on the layer. Used to
    /// gate WMTS capabilities so an unusable time dimension is never advertised
    /// when layer metadata stores a non-existent or wrong-typed field name.
    /// Mirrors the resolution rules used by
    /// <see cref="TryResolveTemporalRangeAsync"/> (no fallback when
    /// <c>StartTimeField</c> is missing) so capabilities and the request path
    /// agree on whether the dimension is usable.
    /// </summary>
    public static bool HasOptInTemporalFields(LayerDefinition layer)
        => TryResolveOptInTemporalFields(layer, out _);

    /// <summary>
    /// Strict opt-in resolver that returns the resolved start/end fields when
    /// the layer is opt-in time-aware AND every configured temporal field
    /// resolves to a <c>Date</c>/<c>DateTime</c> attribute. Mirrors the
    /// no-fallback rules used by <see cref="TryResolveTemporalRangeAsync"/> so
    /// capability advertising and request validation share a single source of
    /// truth (used by WMS GetMap and WMTS GetTile/GetFeatureInfo TIME parsing
    /// to avoid accepting requests for layers that capabilities will not
    /// advertise).
    /// </summary>
    public static bool TryResolveOptInTemporalFields(
        LayerDefinition layer,
        out TemporalFieldSelection selection)
    {
        selection = default;
        var timeInfo = layer.Metadata?.TimeInfo;
        if (timeInfo is null || string.IsNullOrWhiteSpace(timeInfo.StartTimeField))
        {
            return false;
        }

        if (!TryResolveTemporalFields(
                layer,
                allowFallbackWhenMissingStart: false,
                out var startField,
                out var endField,
                out _,
                out _))
        {
            return false;
        }

        selection = new TemporalFieldSelection(startField!, endField);
        return true;
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

        // Min comes from the earliest configured start; max effectively
        // tracks COALESCE(end, start) so an interval-configured layer whose
        // end column is null on every row still advertises the latest start
        // timestamp. Without this fallback temporalExtent / WMS-WMTS
        // <Default> would lose max for valid instant-style rows on layers
        // where the operator configured an end field that no row has set.
        var min = startExtent?.Start;
        DateTimeOffset? max;
        if (endField == null)
        {
            max = startExtent?.End;
        }
        else
        {
            max = endExtent?.End ?? endExtent?.Start ?? startExtent?.End;
        }

        var hasExtent = startExtent != null || endExtent != null;
        return new TemporalRange(startField, endField, min, max, hasExtent);
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

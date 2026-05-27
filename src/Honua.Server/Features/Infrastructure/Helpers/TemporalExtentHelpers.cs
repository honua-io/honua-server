// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

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

    /// <summary>
    /// V2 parallel of <see cref="TemporalRange"/> — the start and end fields are name strings
    /// instead of v1 <see cref="FieldDefinition"/> objects.
    /// </summary>
    internal readonly record struct MetadataV2TemporalRange(
        string StartFieldName,
        string? EndFieldName,
        DateTimeOffset? Min,
        DateTimeOffset? Max,
        bool HasExtent);

    internal readonly record struct TemporalFieldSelection(
        FieldDefinition StartField,
        FieldDefinition? EndField);

    /// <summary>
    /// V2 parallel of <see cref="TemporalFieldSelection"/> — carries the field name strings
    /// instead of v1 <see cref="FieldDefinition"/> records.
    /// </summary>
    internal readonly record struct MetadataV2TemporalFieldSelection(
        string StartFieldName,
        string? EndFieldName);

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
    /// V2 overload of <see cref="HasOptInTemporalFields(LayerDefinition)"/>. Returns true when
    /// the resource declares a configured <c>startTimeField</c> via
    /// <see cref="MetadataV2Resource.Temporal"/> AND that field resolves to a
    /// date/datetime entry in <see cref="MetadataV2Resource.SchemaFields"/>.
    /// </summary>
    public static bool HasOptInTemporalFields(MetadataV2Resource resource)
        => TryResolveOptInTemporalFieldsV2(resource, out _);

    /// <summary>
    /// V2 overload of <see cref="TryResolveOptInTemporalFields(LayerDefinition, out TemporalFieldSelection)"/>.
    /// Resolves the configured opt-in temporal field names from the resource's temporal
    /// extension; returns false (selection=default) when the resource has no
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

        // Read-only providers (MySQL/MariaDB, SQL Server) throw
        // NotSupportedException from GetTemporalExtentAsync. Treat that as
        // "no extent available" so capabilities/temporalExtent paths fall
        // back to their non-time-aware contract (omit time dimension, return
        // 404, etc.) instead of bubbling a 500 to the client. The layer is
        // still time-aware in metadata terms; only extent discovery is
        // unsupported on the backing store.
        TemporalExtentResult? startExtent;
        try
        {
            startExtent = await featureReader.GetTemporalExtentAsync(
                layer.Id,
                startField!.Name,
                startField.Type,
                cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return null;
        }

        TemporalExtentResult? endExtent = null;
        if (endField != null && !endField.Name.Equals(startField.Name, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                endExtent = await featureReader.GetTemporalExtentAsync(
                    layer.Id,
                    endField.Name,
                    endField.Type,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                return null;
            }
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

    /// <summary>
    /// V2 overload of <see cref="TryResolveTemporalRangeAsync(LayerDefinition, IFeatureReader, CancellationToken)"/>.
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

        var startFieldType = ResolveSchemaFieldType(resource, fields.StartTimeField);
        TemporalExtentResult? startExtent;
        try
        {
            startExtent = await featureReader.GetTemporalExtentAsync(
                layerIndex,
                fields.StartTimeField,
                startFieldType,
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
            var endFieldType = ResolveSchemaFieldType(resource, fields.EndTimeField);
            try
            {
                endExtent = await featureReader.GetTemporalExtentAsync(
                    layerIndex,
                    fields.EndTimeField,
                    endFieldType,
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

    private static FieldType ResolveSchemaFieldType(MetadataV2Resource resource, string fieldName)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return field.Type switch
                {
                    MetadataV2FieldType.Date => FieldType.Date,
                    MetadataV2FieldType.Time => FieldType.Time,
                    _ => FieldType.DateTime,
                };
            }
        }
        return FieldType.DateTime;
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

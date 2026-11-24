// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Protocols.GeoServices.FeatureServer.Services;

namespace Honua.Protocols.GeoServices.MapServer;

internal readonly record struct LayerTimeTarget(int Id, int PublicLayerId);

internal sealed record LayerTimeOptions(
    bool? UseTime,
    bool TimeDataCumulative,
    double? TimeOffset,
    string? TimeOffsetUnits,
    string? Time,
    string? TimeRelation);

internal static class GeoServicesLayerTimeResolver
{
    internal const string InvalidTimeParameterMessage = "Invalid time parameter.";

    internal static string? NormalizeTimeRelation(string? timeRelation)
    {
        if (string.IsNullOrWhiteSpace(timeRelation))
        {
            return null;
        }

        var trimmed = timeRelation.Trim();
        if (string.Equals(trimmed, "esriTimeRelationAfterStartOverlapsEnd", StringComparison.OrdinalIgnoreCase))
        {
            return "esriTimeRelationOverlapsEndWithinStart";
        }

        return trimmed;
    }

    internal static bool TryGetEffectiveTimeParameters(
        string? globalTime,
        string? globalTimeRelation,
        LayerTimeTarget target,
        IReadOnlyDictionary<int, LayerTimeOptions> layerTimeOptions,
        out string? effectiveTime,
        out string? effectiveTimeRelation,
        out string? error)
    {
        effectiveTime = globalTime;
        effectiveTimeRelation = globalTimeRelation;
        error = null;

        LayerTimeOptions? options = null;
        if (TryResolveLayerTimeOptions(layerTimeOptions, target, out var resolvedOptions))
        {
            options = resolvedOptions;
            if (options.UseTime is false)
            {
                effectiveTime = null;
                effectiveTimeRelation = null;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(options.Time))
            {
                effectiveTime = options.Time;
            }

            if (!string.IsNullOrWhiteSpace(options.TimeRelation))
            {
                effectiveTimeRelation = options.TimeRelation;
            }
        }

        if (string.IsNullOrWhiteSpace(effectiveTime))
        {
            effectiveTime = null;
            effectiveTimeRelation = null;
            return true;
        }

        if (!GeoServicesTemporalQueryBuilder.TryParseTimeParameter(effectiveTime, out var start, out var end))
        {
            error = InvalidTimeParameterMessage;
            return false;
        }

        // time=null,null parses as both bounds null — the documented no-op
        // form. Treat it identically to an omitted time parameter so layer
        // rendering does not invent a degenerate filter.
        if (start is null && end is null && options?.TimeDataCumulative != true)
        {
            effectiveTime = null;
            effectiveTimeRelation = null;
            return true;
        }

        if (options?.TimeDataCumulative == true)
        {
            start = null;
        }

        if (options?.TimeOffset is { } timeOffset)
        {
            if (!TryApplyTimeOffset(
                    start,
                    end,
                    timeOffset,
                    options.TimeOffsetUnits,
                    out var adjustedStart,
                    out var adjustedEnd,
                    out var offsetError))
            {
                error = offsetError ?? "Invalid time offset.";
                return false;
            }

            start = adjustedStart;
            end = adjustedEnd;
        }

        effectiveTime = BuildTimeParameter(start, end);
        effectiveTimeRelation = NormalizeTimeRelation(effectiveTimeRelation);
        return true;
    }

    private static bool TryResolveLayerTimeOptions(
        IReadOnlyDictionary<int, LayerTimeOptions> layerTimeOptions,
        LayerTimeTarget target,
        out LayerTimeOptions resolvedOptions)
    {
        if (layerTimeOptions.TryGetValue(target.Id, out var directOptions) &&
            directOptions is not null)
        {
            resolvedOptions = directOptions;
            return true;
        }

        if (target.Id != target.PublicLayerId &&
            layerTimeOptions.TryGetValue(target.PublicLayerId, out var sourceLayerOptions) &&
            sourceLayerOptions is not null)
        {
            resolvedOptions = sourceLayerOptions;
            return true;
        }

        resolvedOptions = default!;
        return false;
    }

    private static string BuildTimeParameter(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start.HasValue && end.HasValue)
        {
            var startValue = start.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var endValue = end.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            return $"{startValue},{endValue}";
        }

        if (start.HasValue)
        {
            return start.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        if (end.HasValue)
        {
            var endValue = end.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            return $",{endValue}";
        }

        throw new ArgumentException("Time parameter requires a start or end time.");
    }

    private static bool TryApplyTimeOffset(
        DateTimeOffset? start,
        DateTimeOffset? end,
        double offset,
        string? units,
        out DateTimeOffset? adjustedStart,
        out DateTimeOffset? adjustedEnd,
        out string? error)
    {
        adjustedStart = start;
        adjustedEnd = end;
        error = null;

        if (string.IsNullOrWhiteSpace(units))
        {
            error = "timeOffsetUnits is required when timeOffset is specified.";
            return false;
        }

        var normalizedUnits = units.Trim();
        var signedOffset = -offset;

        switch (normalizedUnits.ToLowerInvariant())
        {
            case "esritimeunitsyears":
            case "years":
            case "year":
                {
                    if (!TryToIntegerOffset(offset, out var intOffset, out error))
                    {
                        return false;
                    }

                    adjustedStart = start?.AddYears(-intOffset);
                    adjustedEnd = end?.AddYears(-intOffset);
                    return true;
                }
            case "esritimeunitsmonths":
            case "months":
            case "month":
                {
                    if (!TryToIntegerOffset(offset, out var intOffset, out error))
                    {
                        return false;
                    }

                    adjustedStart = start?.AddMonths(-intOffset);
                    adjustedEnd = end?.AddMonths(-intOffset);
                    return true;
                }
            case "esritimeunitsweeks":
            case "weeks":
            case "week":
                {
                    var span = TimeSpan.FromDays(7 * signedOffset);
                    adjustedStart = start?.Add(span);
                    adjustedEnd = end?.Add(span);
                    return true;
                }
            case "esritimeunitsdays":
            case "days":
            case "day":
                {
                    var span = TimeSpan.FromDays(signedOffset);
                    adjustedStart = start?.Add(span);
                    adjustedEnd = end?.Add(span);
                    return true;
                }
            case "esritimeunitshours":
            case "hours":
            case "hour":
                {
                    var span = TimeSpan.FromHours(signedOffset);
                    adjustedStart = start?.Add(span);
                    adjustedEnd = end?.Add(span);
                    return true;
                }
            case "esritimeunitsminutes":
            case "minutes":
            case "minute":
                {
                    var span = TimeSpan.FromMinutes(signedOffset);
                    adjustedStart = start?.Add(span);
                    adjustedEnd = end?.Add(span);
                    return true;
                }
            case "esritimeunitsseconds":
            case "seconds":
            case "second":
                {
                    var span = TimeSpan.FromSeconds(signedOffset);
                    adjustedStart = start?.Add(span);
                    adjustedEnd = end?.Add(span);
                    return true;
                }
            case "esritimeunitsmilliseconds":
            case "milliseconds":
            case "millisecond":
                {
                    var span = TimeSpan.FromMilliseconds(signedOffset);
                    adjustedStart = start?.Add(span);
                    adjustedEnd = end?.Add(span);
                    return true;
                }
            default:
                error = $"Unsupported timeOffsetUnits value '{units}'.";
                return false;
        }
    }

    private static bool TryToIntegerOffset(double offset, out int value, out string? error)
    {
        error = null;
        var rounded = Math.Round(offset, MidpointRounding.AwayFromZero);
        if (Math.Abs(offset - rounded) > 0.000001)
        {
            value = 0;
            error = "timeOffset for month/year units must be an integer.";
            return false;
        }

        value = (int)rounded;
        return true;
    }
}

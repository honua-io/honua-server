// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Protocols.GeoServices.FeatureServer.Services;

namespace Honua.Protocols.GeoServices.MapServer;

internal readonly record struct LayerTimeTarget(int Id, int PublicLayerId);

internal sealed record LayerTimeOptions
{
    public bool? UseTime { get; init; }
    public bool TimeDataCumulative { get; init; }
    public double? TimeOffset { get; init; }
    public string? TimeOffsetUnits { get; init; }
    public string? Time { get; init; }
    public string? TimeRelation { get; init; }
}

internal readonly record struct EffectiveTimeRequest
{
    public string? GlobalTime { get; init; }
    public string? GlobalTimeRelation { get; init; }
    public required LayerTimeTarget Target { get; init; }
    public required IReadOnlyDictionary<int, LayerTimeOptions> LayerTimeOptions { get; init; }
}

internal readonly record struct EffectiveTimeResolution
{
    public bool IsValid { get; init; }
    public string? EffectiveTime { get; init; }
    public string? EffectiveTimeRelation { get; init; }
    public string? Error { get; init; }
}

internal static class GeoServicesLayerTimeResolver
{
    internal const string InvalidTimeParameterMessage = "Invalid time parameter.";

    private readonly record struct TimeRangeOffsetRequest
    {
        public DateTimeOffset? Start { get; init; }
        public DateTimeOffset? End { get; init; }
        public required double Offset { get; init; }
        public string? Units { get; init; }
    }

    private readonly record struct TimeRangeOffsetResult
    {
        public bool IsValid { get; init; }
        public DateTimeOffset? Start { get; init; }
        public DateTimeOffset? End { get; init; }
        public string? Error { get; init; }
    }

    private readonly record struct IntegerOffsetResult
    {
        public bool IsValid { get; init; }
        public int Value { get; init; }
        public string? Error { get; init; }
    }

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

    internal static EffectiveTimeResolution ResolveEffectiveTimeParameters(EffectiveTimeRequest request)
    {
        var options = ResolveLayerTimeOptions(request.LayerTimeOptions, request.Target);
        if (options?.UseTime is false)
        {
            return new EffectiveTimeResolution { IsValid = true };
        }

        var effectiveTime = !string.IsNullOrWhiteSpace(options?.Time) ? options.Time : request.GlobalTime;
        var effectiveTimeRelation = !string.IsNullOrWhiteSpace(options?.TimeRelation)
            ? options.TimeRelation
            : request.GlobalTimeRelation;

        if (string.IsNullOrWhiteSpace(effectiveTime))
        {
            return new EffectiveTimeResolution { IsValid = true };
        }

        return BuildResolvedTimeParameters(effectiveTime, effectiveTimeRelation, options);
    }

    private static EffectiveTimeResolution BuildResolvedTimeParameters(
        string effectiveTime,
        string? effectiveTimeRelation,
        LayerTimeOptions? options)
    {
        if (!GeoServicesTemporalQueryBuilder.TryParseTimeParameter(effectiveTime, out var start, out var end))
            return new EffectiveTimeResolution { IsValid = false, Error = InvalidTimeParameterMessage };

        if (start is null && end is null && options?.TimeDataCumulative != true)
            return new EffectiveTimeResolution { IsValid = true };

        if (options?.TimeDataCumulative == true)
            start = null;

        if (options?.TimeOffset is { } timeOffset)
        {
            var offsetResult = ApplyTimeOffset(new TimeRangeOffsetRequest
            {
                Start = start,
                End = end,
                Offset = timeOffset,
                Units = options.TimeOffsetUnits
            });
            if (!offsetResult.IsValid)
                return new EffectiveTimeResolution { IsValid = false, Error = offsetResult.Error ?? "Invalid time offset." };

            (start, end) = (offsetResult.Start, offsetResult.End);
        }

        return new EffectiveTimeResolution
        {
            IsValid = true,
            EffectiveTime = BuildTimeParameter(start, end),
            EffectiveTimeRelation = NormalizeTimeRelation(effectiveTimeRelation)
        };
    }

    private static LayerTimeOptions? ResolveLayerTimeOptions(
        IReadOnlyDictionary<int, LayerTimeOptions> layerTimeOptions,
        LayerTimeTarget target)
    {
        if (layerTimeOptions.TryGetValue(target.Id, out var directOptions) &&
            directOptions is not null)
        {
            return directOptions;
        }

        if (target.Id != target.PublicLayerId &&
            layerTimeOptions.TryGetValue(target.PublicLayerId, out var sourceLayerOptions) &&
            sourceLayerOptions is not null)
        {
            return sourceLayerOptions;
        }

        return null;
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

    private static TimeRangeOffsetResult ApplyTimeOffset(TimeRangeOffsetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Units))
        {
            return new TimeRangeOffsetResult
            {
                IsValid = false,
                Start = request.Start,
                End = request.End,
                Error = "timeOffsetUnits is required when timeOffset is specified."
            };
        }

        var normalizedUnits = request.Units.Trim().ToLowerInvariant();
        if (normalizedUnits is "esritimeunitsyears" or "years" or "year"
            or "esritimeunitsmonths" or "months" or "month")
        {
            return ApplyCalendarOffset(request, normalizedUnits);
        }

        var span = ResolveFixedOffsetSpan(normalizedUnits, -request.Offset);
        if (!span.HasValue)
        {
            return new TimeRangeOffsetResult
            {
                IsValid = false,
                Start = request.Start,
                End = request.End,
                Error = $"Unsupported timeOffsetUnits value '{request.Units}'."
            };
        }

        return new TimeRangeOffsetResult
        {
            IsValid = true,
            Start = request.Start?.Add(span.Value),
            End = request.End?.Add(span.Value)
        };
    }

    private static TimeRangeOffsetResult ApplyCalendarOffset(
        TimeRangeOffsetRequest request,
        string normalizedUnits)
    {
        var integerOffset = ParseIntegerOffset(request.Offset);
        if (!integerOffset.IsValid)
        {
            return new TimeRangeOffsetResult
            {
                IsValid = false,
                Start = request.Start,
                End = request.End,
                Error = integerOffset.Error
            };
        }

        var delta = -integerOffset.Value;
        var isYears = normalizedUnits is "esritimeunitsyears" or "years" or "year";
        return new TimeRangeOffsetResult
        {
            IsValid = true,
            Start = isYears ? request.Start?.AddYears(delta) : request.Start?.AddMonths(delta),
            End = isYears ? request.End?.AddYears(delta) : request.End?.AddMonths(delta)
        };
    }

    private static TimeSpan? ResolveFixedOffsetSpan(string normalizedUnits, double signedOffset) =>
        normalizedUnits switch
        {
            "esritimeunitsweeks" or "weeks" or "week" => TimeSpan.FromDays(7 * signedOffset),
            "esritimeunitsdays" or "days" or "day" => TimeSpan.FromDays(signedOffset),
            "esritimeunitshours" or "hours" or "hour" => TimeSpan.FromHours(signedOffset),
            "esritimeunitsminutes" or "minutes" or "minute" => TimeSpan.FromMinutes(signedOffset),
            "esritimeunitsseconds" or "seconds" or "second" => TimeSpan.FromSeconds(signedOffset),
            "esritimeunitsmilliseconds" or "milliseconds" or "millisecond" => TimeSpan.FromMilliseconds(signedOffset),
            _ => null
        };

    private static IntegerOffsetResult ParseIntegerOffset(double offset)
    {
        var rounded = Math.Round(offset, MidpointRounding.AwayFromZero);
        if (Math.Abs(offset - rounded) > 0.000001)
        {
            return new IntegerOffsetResult
            {
                IsValid = false,
                Error = "timeOffset for month/year units must be an integer."
            };
        }

        return new IntegerOffsetResult { IsValid = true, Value = (int)rounded };
    }
}

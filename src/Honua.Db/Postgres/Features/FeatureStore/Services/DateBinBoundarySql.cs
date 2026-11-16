// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

// Shared by the shared-table and source-backed readers so both bin identically.
internal static class DateBinBoundarySql
{
    // TimeSpan.TotalDays/TotalHours/TotalMinutes are computed by dividing Ticks by a
    // constant, which can leave a whole-unit duration a few ULPs away from its exact
    // integer value. Compare within a tolerance instead of exact float equality so a
    // duration like "7 days" reliably formats as whole days rather than falling through
    // to a fractional/seconds representation.
    private const double WholeUnitTolerance = 1e-9;

    public static string Build(
        DateBinDefinition dateBin,
        string timestampExpression,
        Func<DateTimeOffset, string> addOriginParameter)
    {
        if (dateBin.IsCalendarBin)
        {
            // Truncate in UTC: a registered source database may run in any session time zone.
            return $"date_trunc('{MapCalendarUnit(dateBin.CalendarUnit!)}', {timestampExpression}, 'UTC')";
        }

        if (dateBin.FixedInterval is { } interval)
        {
            var origin = dateBin.FixedIntervalOrigin is { } value
                ? $"{addOriginParameter(value)}::timestamptz"
                : "'1970-01-01T00:00:00Z'::timestamptz";
            return $"date_bin('{FormatInterval(interval)}'::interval, {timestampExpression}, {origin})";
        }

        throw new ArgumentException("DateBinDefinition must specify either CalendarUnit or FixedInterval.", nameof(dateBin));
    }

    private static string MapCalendarUnit(string unit)
    {
        return unit.ToLowerInvariant() switch
        {
            "year" => "year",
            "quarter" => "quarter",
            "month" => "month",
            "week" => "week",
            "day" => "day",
            "hour" => "hour",
            "minute" => "minute",
            "second" => "second",
            _ => "month"
        };
    }

    private static string FormatInterval(TimeSpan interval)
    {
        if (interval.TotalDays >= 1 && IsWholeUnit(interval.TotalDays))
        {
            return FormattableString.Invariant($"{(int)interval.TotalDays} days");
        }

        if (interval.TotalHours >= 1 && IsWholeUnit(interval.TotalHours))
        {
            return FormattableString.Invariant($"{(int)interval.TotalHours} hours");
        }

        if (interval.TotalMinutes >= 1 && IsWholeUnit(interval.TotalMinutes))
        {
            return FormattableString.Invariant($"{(int)interval.TotalMinutes} minutes");
        }

        return FormattableString.Invariant($"{(int)interval.TotalSeconds} seconds");
    }

    private static bool IsWholeUnit(double value)
        => Math.Abs(value - Math.Floor(value)) < WholeUnitTolerance;
}

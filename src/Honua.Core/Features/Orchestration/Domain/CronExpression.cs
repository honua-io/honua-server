// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Minimal 5-field cron expression evaluator. Supports the POSIX subset used by the
/// orchestration scheduler: <c>*</c>, literal values, comma-separated lists,
/// ranges (<c>a-b</c>) and steps (<c>*/n</c>, <c>a-b/n</c>). Day-of-week 0 or 7 means Sunday.
/// </summary>
public sealed class CronExpression
{
    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _daysOfMonth;
    private readonly bool[] _months;
    private readonly bool[] _daysOfWeek;

    private CronExpression(bool[] minutes, bool[] hours, bool[] daysOfMonth, bool[] months, bool[] daysOfWeek, string expression)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        Expression = expression;
    }

    /// <summary>
    /// Original cron expression string.
    /// </summary>
    public string Expression { get; }

    /// <summary>
    /// Parses a cron expression. Throws <see cref="FormatException"/> on invalid input.
    /// </summary>
    public static CronExpression Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            throw new FormatException($"Cron expression must contain exactly 5 fields but got {fields.Length}: '{expression}'.");
        }

        var minutes = ParseField(fields[0], 0, 59);
        var hours = ParseField(fields[1], 0, 23);
        var daysOfMonth = ParseField(fields[2], 1, 31);
        var months = ParseField(fields[3], 1, 12);
        var daysOfWeek = ParseField(fields[4], 0, 7);
        // Day-of-week 7 is a valid alias for Sunday; fold it back onto 0 to keep evaluation simple.
        if (daysOfWeek.Length == 8 && daysOfWeek[7])
        {
            daysOfWeek[0] = true;
        }

        var compact = new bool[7];
        Array.Copy(daysOfWeek, 0, compact, 0, 7);

        return new CronExpression(minutes, hours, daysOfMonth, months, compact, expression);
    }

    /// <summary>
    /// Returns the next fire time strictly greater than <paramref name="after"/> in the
    /// supplied time zone, or null if no matching occurrence exists within 366 days.
    /// </summary>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset after, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var local = TimeZoneInfo.ConvertTime(after, timeZone);
        // Advance to the next minute boundary with :00 seconds. Cron granularity is minutes.
        var candidate = new DateTime(
            local.Year,
            local.Month,
            local.Day,
            local.Hour,
            local.Minute,
            0,
            DateTimeKind.Unspecified).AddMinutes(1);

        var limit = candidate.AddDays(366);
        while (candidate < limit)
        {
            if (!_months[candidate.Month])
            {
                candidate = new DateTime(candidate.Year, candidate.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
                continue;
            }

            if (!_daysOfMonth[candidate.Day] || !_daysOfWeek[(int)candidate.DayOfWeek])
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!_hours[candidate.Hour])
            {
                candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day, candidate.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1);
                continue;
            }

            if (!_minutes[candidate.Minute])
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            var offset = timeZone.GetUtcOffset(candidate);
            return new DateTimeOffset(candidate, offset).ToUniversalTime();
        }

        return null;
    }

    private static bool[] ParseField(string field, int min, int max)
    {
        var length = max - min + 1 + (max == 7 ? 0 : 0);
        var size = max + 1;
        var result = new bool[size];

        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            ParsePart(part.Trim(), min, max, result);
        }

        if (!result.Skip(min).Take(max - min + 1).Any(x => x))
        {
            throw new FormatException($"Cron field '{field}' did not match any values in the range [{min}, {max}].");
        }

        _ = length;
        return result;
    }

    private static void ParsePart(string part, int min, int max, bool[] result)
    {
        if (string.IsNullOrEmpty(part))
        {
            throw new FormatException("Cron expression contains an empty field part.");
        }

        var step = 1;
        var body = part;
        var slashIndex = part.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex >= 0)
        {
            body = part[..slashIndex];
            var stepText = part[(slashIndex + 1)..];
            if (!int.TryParse(stepText, NumberStyles.Integer, CultureInfo.InvariantCulture, out step) || step <= 0)
            {
                throw new FormatException($"Invalid cron step '{stepText}'.");
            }
        }

        int start;
        int end;

        if (body == "*")
        {
            start = min;
            end = max;
        }
        else if (body.Contains('-', StringComparison.Ordinal))
        {
            var rangeParts = body.Split('-');
            if (rangeParts.Length != 2 ||
                !int.TryParse(rangeParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out start) ||
                !int.TryParse(rangeParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
            {
                throw new FormatException($"Invalid cron range '{body}'.");
            }
        }
        else
        {
            if (!int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out start))
            {
                throw new FormatException($"Invalid cron value '{body}'.");
            }

            end = start;
        }

        if (start < min || end > max || start > end)
        {
            throw new FormatException($"Cron value '{body}' is outside the allowed range [{min}, {max}].");
        }

        for (var value = start; value <= end; value += step)
        {
            result[value] = true;
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.Metadata.Domain.V2;

public static partial class ExactAttributeValues
{
    private const string DateFormat = "yyyy'-'MM'-'dd";

    private static readonly string[] TimeFormats =
    [
        "HH':'mm':'ss",
        "HH':'mm':'ss'.'f", "HH':'mm':'ss'.'ff", "HH':'mm':'ss'.'fff", "HH':'mm':'ss'.'ffff",
        "HH':'mm':'ss'.'fffff", "HH':'mm':'ss'.'ffffff", "HH':'mm':'ss'.'fffffff",
    ];

    private static readonly TimeSpan MaxOffset = TimeSpan.FromHours(14);

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    [GeneratedRegex(
        @"^(?<local>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?)(?<zone>Z|(?<sign>[+-])(?<hours>[0-9]{2}):(?<minutes>[0-9]{2}))\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimestampOffsetPattern();

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalPattern();

    public static DateOnly ParseDate(string value)
    {
        if (!DatePattern().IsMatch(value)
            || !DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new ArgumentException($"date_value '{value}' is not a calendar date in YYYY-MM-DD form.");
        }

        return date;
    }

    public static TimeOnly ParseTime(string value)
    {
        // TimeOnly parsing accepts 24:00:00; the wire range ends at 23:59:59.9999999.
        if (!TimePattern().IsMatch(value)
            || value.StartsWith("24", StringComparison.Ordinal)
            || !TimeOnly.TryParseExact(value, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            throw new ArgumentException($"time_value '{value}' is not a local time in HH:mm:ss[.fffffff] form.");
        }

        return time;
    }

    public static DateTimeOffset ParseTimestampOffset(string value)
    {
        var match = TimestampOffsetPattern().Match(value);
        if (!match.Success)
        {
            throw new ArgumentException($"timestamp_offset_value '{value}' is not an RFC 3339 timestamp with an offset.");
        }

        var offset = TimeSpan.Zero;
        if (match.Groups["sign"].Success)
        {
            var hours = int.Parse(match.Groups["hours"].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture);
            offset = new TimeSpan(hours, minutes, 0);
            if (minutes > 59 || offset > MaxOffset)
            {
                throw new ArgumentException($"timestamp_offset_value '{value}' has an offset outside ±14:00.");
            }

            if (match.Groups["sign"].Value == "-")
            {
                offset = offset.Negate();
            }
        }

        var local = match.Groups["local"].Value;
        var date = ParseDate(local[..10]);
        var time = ParseTime(local[11..]);
        try
        {
            return new DateTimeOffset(date.ToDateTime(time, DateTimeKind.Unspecified), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ExactValueRangeException($"timestamp_offset_value '{value}' is outside the representable range.");
        }
    }

    public static decimal ParseDecimal(string value)
    {
        if (!DecimalPattern().IsMatch(value))
        {
            throw new ArgumentException($"decimal_value '{value}' is not an exact base-10 number.");
        }

        // Lexically valid but outside the decimal range: the server cannot hold it.
        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number))
        {
            throw new ExactValueRangeException($"decimal_value '{value}' is outside the supported decimal range.");
        }

        var exact = number.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(exact, value, StringComparison.Ordinal)
            && !string.Equals(exact, NormalizeNegativeZero(value), StringComparison.Ordinal))
        {
            throw new ExactValueRangeException($"decimal_value '{value}' exceeds the supported decimal precision.");
        }

        return number;
    }

    private static string NormalizeNegativeZero(string value)
        => value.StartsWith('-') && value.AsSpan(1).TrimStart('0').TrimStart('.').TrimStart('0').IsEmpty
            ? value[1..]
            : value;
}

public sealed class ExactValueRangeException(string message) : Exception(message);

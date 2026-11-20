// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Xml;

namespace Honua.Core.Features.Raster.ZarrParser;

public static class Iso8601TemporalIntervalParser
{
    public static bool TryParseRange(
        string? datetime,
        out DateTimeOffset? start,
        out DateTimeOffset? end,
        out string? errorMessage)
    {
        start = null;
        end = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(datetime))
        {
            return true;
        }

        if (datetime.Contains(',', StringComparison.Ordinal))
        {
            var tokens = datetime.Split(',', StringSplitOptions.TrimEntries);
            if (tokens.Length == 0 || tokens.Any(string.IsNullOrWhiteSpace))
            {
                errorMessage = "Invalid datetime parameter.";
                return false;
            }

            DateTimeOffset? minStart = null;
            DateTimeOffset? maxEnd = null;
            var hasOpenStart = false;
            var hasOpenEnd = false;

            foreach (var token in tokens)
            {
                if (!TryParseSingleRange(token, out var tokenStart, out var tokenEnd, out errorMessage))
                {
                    return false;
                }

                if (tokenStart is null)
                {
                    hasOpenStart = true;
                }
                else if (minStart is null || tokenStart.Value < minStart.Value)
                {
                    minStart = tokenStart.Value;
                }

                if (tokenEnd is null)
                {
                    hasOpenEnd = true;
                }
                else if (maxEnd is null || tokenEnd.Value > maxEnd.Value)
                {
                    maxEnd = tokenEnd.Value;
                }
            }

            start = hasOpenStart ? null : minStart;
            end = hasOpenEnd ? null : maxEnd;
            return true;
        }

        return TryParseSingleRange(datetime, out start, out end, out errorMessage);
    }

    private static bool TryParseSingleRange(
        string datetime,
        out DateTimeOffset? start,
        out DateTimeOffset? end,
        out string? errorMessage)
    {
        start = null;
        end = null;
        errorMessage = null;

        var parts = datetime.Split('/', StringSplitOptions.TrimEntries);

        if (parts.Length == 1)
        {
            if (!TryParseDateTimeOffset(parts[0], out var instant))
            {
                errorMessage = "Invalid datetime parameter.";
                return false;
            }

            start = instant;
            end = instant;
            return true;
        }

        if (parts.Length is 2 or 3)
        {
            if (parts.Length == 3 && !IsValidPeriodResolution(parts[2]))
            {
                errorMessage = "Invalid datetime parameter.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(parts[0]) && parts[0] != "..")
            {
                if (!TryParseDateTimeOffset(parts[0], out var parsedStart))
                {
                    errorMessage = "Invalid datetime parameter.";
                    return false;
                }

                start = parsedStart;
            }

            if (!string.IsNullOrWhiteSpace(parts[1]) && parts[1] != "..")
            {
                if (!TryParseDateTimeOffset(parts[1], out var parsedEnd))
                {
                    errorMessage = "Invalid datetime parameter.";
                    return false;
                }

                end = parsedEnd;
            }

            if (start is null && end is null)
            {
                errorMessage = "Invalid datetime parameter.";
                return false;
            }

            if (parts.Length == 3 && (start is null || end is null))
            {
                errorMessage = "Invalid datetime parameter.";
                return false;
            }

            if (start is { } s && end is { } e && s > e)
            {
                errorMessage = "Invalid datetime parameter.";
                return false;
            }

            return true;
        }

        errorMessage = "Invalid datetime parameter.";
        return false;
    }

    private static bool IsValidPeriodResolution(string resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
        {
            return false;
        }

        if (string.Equals(resolution, "0", StringComparison.Ordinal))
        {
            return true;
        }

        if (resolution[0] is not ('P' or 'p'))
        {
            return false;
        }

        try
        {
            return XmlConvert.ToTimeSpan(resolution.ToUpperInvariant()) > TimeSpan.Zero;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryParseDateTimeOffset(string value, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);
}

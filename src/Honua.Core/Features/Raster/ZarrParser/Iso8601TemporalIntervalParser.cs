// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

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

        if (parts.Length == 2)
        {
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

    private static bool TryParseDateTimeOffset(string value, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);
}

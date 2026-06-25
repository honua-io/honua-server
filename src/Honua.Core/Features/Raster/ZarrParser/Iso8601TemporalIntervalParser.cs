// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// Neutral, protocol-agnostic parser for an RFC 3339 / ISO 8601 datetime parameter
/// expressed as an instant or a half-open interval. Lives in <see cref="Honua.Core"/>
/// so coverage/datacube tile rendering and the OGC protocol adapters can share one
/// implementation instead of any protocol family taking a dependency on another's
/// temporal parser. Pure and AOT-safe.
/// </summary>
public static class Iso8601TemporalIntervalParser
{
    /// <summary>
    /// Parses an OGC-style datetime parameter into a <c>(start, end)</c> pair without
    /// requiring a layer or any protocol context. Supported forms: a single instant,
    /// <c>start/end</c>, <c>../end</c>, and <c>start/..</c>. For an instant <c>T</c>,
    /// both <paramref name="start"/> and <paramref name="end"/> are set to <c>T</c>.
    /// An empty/whitespace input is accepted and resolves to <c>(null, null)</c>.
    /// </summary>
    /// <param name="datetime">The raw datetime parameter, or null.</param>
    /// <param name="start">Resolved interval start, or null when open/unset.</param>
    /// <param name="end">Resolved interval end, or null when open/unset.</param>
    /// <param name="errorMessage">Client-safe error when the method returns false.</param>
    /// <returns>True when the value is empty or a well-formed instant/interval.</returns>
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

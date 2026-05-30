// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Infrastructure.Parsing;

internal static class SpanParsingExtensions
{
    internal static bool TryParseDoubles(
        this ReadOnlySpan<char> value,
        Span<double> destination,
        ReadOnlySpan<char> separators,
        out int parsedCount,
        out string? error)
    {
        parsedCount = 0;
        error = null;

        if (value.IsEmpty)
        {
            error = "Value list is empty.";
            return false;
        }

        var index = 0;
        while (index < value.Length)
        {
            while (index < value.Length && IsSeparator(value[index], separators))
            {
                index++;
            }

            if (index >= value.Length)
            {
                break;
            }

            var tokenStart = index;
            while (index < value.Length && !IsSeparator(value[index], separators))
            {
                index++;
            }

            var token = value.Slice(tokenStart, index - tokenStart).Trim();
            if (token.IsEmpty)
            {
                continue;
            }

            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                error = $"Invalid coordinate value: {token.ToString()}";
                return false;
            }

            if (parsedCount < destination.Length)
            {
                destination[parsedCount] = parsed;
            }

            parsedCount++;
        }

        if (parsedCount == 0)
        {
            error = "Value list is empty.";
            return false;
        }

        return true;
    }

    private static bool IsSeparator(char candidate, ReadOnlySpan<char> separators)
        => separators.Contains(candidate);
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Headers;

namespace Honua.Protocols.Scene;

internal static class CopcRangeParser
{
    public static bool TryParseSingleByteRange(
        string headerValue,
        long totalSize,
        out long start,
        out long end)
    {
        start = 0;
        end = 0;

        if (totalSize <= 0
            || string.IsNullOrWhiteSpace(headerValue)
            || !RangeHeaderValue.TryParse(headerValue, out var parsed)
            || !string.Equals(parsed.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || parsed.Ranges.Count != 1)
        {
            return false;
        }

        var range = parsed.Ranges.First();
        if (range.From.HasValue)
        {
            start = range.From.Value;
            if (start < 0 || start >= totalSize)
            {
                return false;
            }

            end = range.To.HasValue
                ? Math.Min(range.To.Value, totalSize - 1)
                : totalSize - 1;
            return start <= end && end < totalSize;
        }

        if (range.To.HasValue)
        {
            var suffix = range.To.Value;
            if (suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, totalSize - suffix);
            end = totalSize - 1;
            return start <= end;
        }

        return false;
    }
}

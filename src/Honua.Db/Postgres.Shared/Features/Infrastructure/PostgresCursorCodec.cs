// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// Shared opaque cursor encoding helpers for Postgres-backed paged readers.
/// </summary>
internal static class PostgresCursorCodec
{
    public static string Encode(DateTimeOffset timestamp, string suffix)
    {
        ArgumentException.ThrowIfNullOrEmpty(suffix);

        return Base64Url.Encode(string.Concat(
            timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture),
            ":",
            suffix));
    }

    public static bool TryDecodeTimestampAndSuffix(
        string? cursor,
        out DateTimeOffset timestamp,
        out string suffix)
    {
        timestamp = default;
        suffix = string.Empty;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        if (!Base64Url.TryDecode(cursor, out var decoded))
        {
            return false;
        }

        var separator = decoded.IndexOf(':');
        if (separator <= 0 || separator == decoded.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(
                decoded.AsSpan(0, separator),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var utcTicks) ||
            utcTicks < DateTimeOffset.MinValue.UtcTicks ||
            utcTicks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        timestamp = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        suffix = decoded[(separator + 1)..];
        return true;
    }
}

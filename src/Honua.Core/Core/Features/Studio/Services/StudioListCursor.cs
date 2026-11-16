// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Core.Features.Studio.Services;

internal static class StudioListCursor
{
    public static string Encode(DateTimeOffset updatedAt, Guid id)
    {
        var raw = string.Concat(updatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), ":", id.ToString("D"));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    public static bool TryDecode(string? cursor, out DateTimeOffset updatedAt, out Guid id)
    {
        updatedAt = default;
        id = default;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separator = raw.IndexOf(':');
            if (separator <= 0 || separator == raw.Length - 1)
            {
                return false;
            }

            if (!long.TryParse(
                    raw.AsSpan(0, separator),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var utcTicks) ||
                utcTicks < DateTimeOffset.MinValue.UtcTicks ||
                utcTicks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }

            if (!Guid.TryParse(raw[(separator + 1)..], out var parsedId))
            {
                return false;
            }

            updatedAt = new DateTimeOffset(utcTicks, TimeSpan.Zero);
            id = parsedId;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

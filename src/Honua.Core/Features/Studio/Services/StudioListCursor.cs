// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Core.Features.Studio.Services;

/// <summary>
/// Opaque keyset cursor codec shared by the Studio content item and package draft list
/// stores (in-memory and Postgres). Encodes an <c>(updatedAt, id)</c> pair so pagination
/// stays stable under concurrent inserts/updates, mirroring the
/// <c>(timestamp, auditId)</c> keyset cursor used by the audit log reader.
/// </summary>
internal static class StudioListCursor
{
    /// <summary>Encodes a cursor from the last kept row's <c>updatedAt</c> and id.</summary>
    public static string Encode(DateTimeOffset updatedAt, Guid id)
    {
        var raw = string.Concat(updatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), ":", id.ToString("D"));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>Attempts to decode a previously encoded cursor. Returns false for a missing or malformed cursor.</summary>
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

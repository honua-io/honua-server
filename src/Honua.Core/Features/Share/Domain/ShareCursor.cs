// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Honua.Core.Features.Share.Domain;

/// <summary>
/// Opaque keyset-pagination cursor over a <c>(timestamp, id)</c> ordering. Shared by the
/// in-memory and Postgres Share export stores so cursor encoding stays identical across
/// providers.
/// </summary>
public static class ShareCursor
{
    private const string LosslessPrefix = "v2";
    private const char Separator = ':';

    /// <summary>
    /// Encodes a keyset position into an opaque, URL-safe cursor token.
    /// </summary>
    /// <param name="timestamp">Ordering timestamp of the last item on the page.</param>
    /// <param name="id">Tiebreak identifier of the last item on the page.</param>
    /// <returns>An opaque cursor token.</returns>
    public static string Encode(DateTimeOffset timestamp, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var raw = string.Concat(
            LosslessPrefix,
            Separator,
            timestamp.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            Separator,
            id);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>
    /// Attempts to decode an opaque cursor token into its keyset position.
    /// </summary>
    /// <param name="cursor">Cursor token to decode.</param>
    /// <param name="timestamp">Decoded ordering timestamp.</param>
    /// <param name="id">Decoded tiebreak identifier.</param>
    /// <returns><c>true</c> when the cursor was well-formed; otherwise <c>false</c>.</returns>
    public static bool TryDecode(string? cursor, out DateTimeOffset timestamp, out string id)
    {
        timestamp = default;
        id = string.Empty;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        var buffer = new byte[Base64.GetMaxDecodedFromUtf8Length(cursor.Length)];
        if (!Convert.TryFromBase64String(cursor, buffer, out var written))
        {
            return false;
        }

        var raw = Encoding.UTF8.GetString(buffer, 0, written);
        if (raw.StartsWith(string.Concat(LosslessPrefix, Separator), StringComparison.Ordinal))
        {
            return TryDecodeLossless(raw, out timestamp, out id);
        }

        var separator = raw.IndexOf(Separator);
        if (separator <= 0 || separator >= raw.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(
                raw.AsSpan(0, separator),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var unixMs))
        {
            return false;
        }

        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        id = raw[(separator + 1)..];
        return true;
    }

    private static bool TryDecodeLossless(string raw, out DateTimeOffset timestamp, out string id)
    {
        timestamp = default;
        id = string.Empty;

        var valueStart = LosslessPrefix.Length + 1;
        var separator = raw.IndexOf(Separator, valueStart);
        if (separator <= valueStart || separator >= raw.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(
                raw.AsSpan(valueStart, separator - valueStart),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var utcTicks))
        {
            return false;
        }

        try
        {
            timestamp = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        id = raw[(separator + 1)..];
        return true;
    }
}

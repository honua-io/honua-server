// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.ControlPlane;

/// <summary>
/// Position of the last job returned by a newest-first page: creation time at the database's
/// microsecond precision, then operation id.
/// </summary>
internal readonly record struct ExecutionJobCursor(DateTimeOffset CreatedAt, string OperationId)
{
    private const char Separator = '|';
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TicksPerMicrosecond), TimeSpan.Zero);
    }

    public string Encode()
    {
        var microseconds = (TruncateToMicroseconds(CreatedAt).Ticks - DateTimeOffset.UnixEpoch.Ticks) / TicksPerMicrosecond;
        var value = string.Concat(microseconds.ToString(CultureInfo.InvariantCulture), Separator.ToString(), OperationId);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    public static bool TryDecode(string cursor, out ExecutionJobCursor decoded)
    {
        decoded = default;
        string text;
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = text.IndexOf(Separator, StringComparison.Ordinal);
        if (separator <= 0 || separator == text.Length - 1
            || !long.TryParse(text.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
        {
            return false;
        }

        long ticks;
        try
        {
            ticks = checked(DateTimeOffset.UnixEpoch.Ticks + (microseconds * TicksPerMicrosecond));
        }
        catch (OverflowException)
        {
            return false;
        }

        if (ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks)
        {
            return false;
        }

        decoded = new ExecutionJobCursor(new DateTimeOffset(ticks, TimeSpan.Zero), text[(separator + 1)..]);
        return true;
    }
}

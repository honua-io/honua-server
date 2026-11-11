// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.ControlPlane;

// Opaque cursor over a job log: the absolute position of the next entry to read.
internal static class ExecutionLogCursor
{
    public const string InvalidCursorMessage = "Invalid log cursor.";

    public static string Encode(long position)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(position.ToString(CultureInfo.InvariantCulture)));

    public static bool TryDecode(string? cursor, out long position)
    {
        position = 0;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return long.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out position) && position >= 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// Minimal URL-safe base64 helpers used by Postgres cursor encoders.
/// </summary>
internal static class Base64Url
{
    public static string Encode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var base64 = Convert.ToBase64String(bytes);
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static bool TryDecode(string value, out string decoded)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 0: break;
            default:
                decoded = string.Empty;
                return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(s);
            decoded = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            decoded = string.Empty;
            return false;
        }
    }
}

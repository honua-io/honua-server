// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

internal static class GdalSrsToken
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var token = value.Trim();
        if (int.TryParse(token, out var epsg) && epsg > 0)
        {
            return true;
        }

        var parts = token.Split(':', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        var authority = parts[0];
        var code = parts[1];
        if (authority.Length is 0 or > 16 || !authority.All(char.IsLetterOrDigit))
        {
            return false;
        }

        return code.Length is > 0 and <= 16 && code.All(char.IsLetterOrDigit);
    }

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var token = value.Trim();
        return int.TryParse(token, out _) ? $"EPSG:{token}" : token;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Shared CRS-token validation and normalization for the native-profile GDAL
/// executors that pass a spatial-reference token to the CLI (<c>gdalwarp</c>,
/// <c>ogr2ogr</c>). Centralized so every executor enforces the same conservative
/// allow-shape — a bare positive integer (EPSG code) or a short
/// <c>AUTHORITY:CODE</c> token — before a value reaches an argument list. This
/// blocks shell-influencing values and arbitrary PROJ strings while covering the
/// reprojection and datum-shift cases (EPSG codes, ESRI / IGNF datum authorities).
/// </summary>
internal static class GdalSrsToken
{
    /// <summary>
    /// Validates a CRS token to the conservative allow-shape. Accepts a bare
    /// positive integer (EPSG code) or an <c>AUTHORITY:CODE</c> token whose
    /// authority and code are alphanumeric and bounded in length.
    /// </summary>
    /// <param name="value">The raw CRS token from the durable spec.</param>
    /// <returns><c>true</c> when the token is safe to pass to the CLI.</returns>
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

    /// <summary>
    /// Normalizes a validated CRS token for the CLI: a bare integer is widened to
    /// the <c>EPSG:&lt;code&gt;</c> form; an <c>AUTHORITY:CODE</c> token is passed
    /// through trimmed. Callers must validate with <see cref="IsValid(string?)"/>
    /// first.
    /// </summary>
    /// <param name="value">A token already accepted by <see cref="IsValid(string?)"/>.</param>
    /// <returns>The normalized CLI-ready CRS token.</returns>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var token = value.Trim();
        return int.TryParse(token, out _) ? $"EPSG:{token}" : token;
    }
}

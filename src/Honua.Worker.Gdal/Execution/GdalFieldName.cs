// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Shared attribute-name allow-shape for the raster&lt;-&gt;vector executors
/// (<c>conversion.polygonize</c> field name, <c>conversion.rasterize</c> burn
/// attribute; #2240). Restricts identifiers to <c>^[A-Za-z_][A-Za-z0-9_]*$</c> so a
/// value cannot influence CLI flag parsing or downstream SQL/OGR field handling.
/// </summary>
internal static class GdalFieldName
{
    /// <summary>Returns whether <paramref name="value"/> is a safe attribute identifier.</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var token = value.Trim();
        if (token.Length > 128)
        {
            return false;
        }

        if (!(char.IsAsciiLetter(token[0]) || token[0] == '_'))
        {
            return false;
        }

        return token.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }
}

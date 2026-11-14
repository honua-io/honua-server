// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.TileOperations;

internal static class GeneratedTileCacheKey
{
    public static string Sanitize(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "segment";
        }

        var chars = new char[trimmed.Length];
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            chars[i] = char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? char.ToLowerInvariant(ch) : '_';
        }

        return new string(chars);
    }
}

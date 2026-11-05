// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Server.Features.Admin.TileOperations;

internal readonly record struct ParsedGeneratedTileKey(
    string? PublicationScope,
    int LayerId,
    string Gridset,
    string Style,
    string Format,
    int Z,
    int X,
    int Y);

// Expire, delete, and inventory parse keys through this one helper so a second derivation cannot miss the window or exceed it.
internal static class GeneratedTileCacheKey
{
    // Keys that are not generated tiles never match, and service-scoped lifecycle fails closed on legacy keys that have no publication identity.
    public static bool TryParse(string key, out ParsedGeneratedTileKey parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 7)
        {
            return false;
        }

        var fileSegment = segments[^1];
        var dot = fileSegment.LastIndexOf('.');
        if (dot <= 0 || dot >= fileSegment.Length - 1)
        {
            return false;
        }

        var rowText = fileSegment[..dot];
        var format = fileSegment[(dot + 1)..];

        if (!int.TryParse(rowText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            || !int.TryParse(segments[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(segments[^3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)
            || !int.TryParse(segments[^7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return false;
        }

        var publicationScope = segments.Length >= 9
            && string.Equals(segments[^9], "v2", StringComparison.Ordinal)
                ? segments[^8]
                : null;

        parsed = new ParsedGeneratedTileKey(
            publicationScope,
            layerId,
            segments[^6],
            segments[^5],
            format,
            z,
            x,
            y);
        return true;
    }

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

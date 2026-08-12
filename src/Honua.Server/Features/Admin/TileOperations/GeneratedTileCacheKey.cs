// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// A parsed generated tile-cache object key. The generated cache stores loose per-tile objects whose
/// key encodes every window-scoping dimension as trailing path segments
/// (<c>[prefix.../]{layerId}/{gridset}/{style}/{hash}/{z}/{x}/{y}.{format}</c>), so a bounded
/// lifecycle operation (issue #2661) can reconstruct the window from the tracked keys alone.
/// </summary>
internal readonly record struct ParsedGeneratedTileKey(
    int LayerId,
    string Gridset,
    string Style,
    string Format,
    int Z,
    int X,
    int Y);

/// <summary>
/// The single object-key convention for the generated tile cache. Both the bounded expire/delete
/// window (<see cref="TileOperationExecutionCore"/>) and the read-only inventory endpoint parse keys
/// through this one helper so a mis-derivation cannot silently no-op or over-reach — the top risk
/// called out for issue #2661.
/// </summary>
internal static class GeneratedTileCacheKey
{
    /// <summary>
    /// Parses a generated tile-cache object key from its tail so the parse is independent of any
    /// provider key prefix. Returns <c>false</c> for keys that are not shaped like a generated tile
    /// (fewer than seven segments, or non-numeric layer/level/col/row), which are then never matched.
    /// </summary>
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

        parsed = new ParsedGeneratedTileKey(
            layerId,
            segments[^6],
            segments[^5],
            format,
            z,
            x,
            y);
        return true;
    }

    /// <summary>
    /// Normalizes a gridset/style/format value to the form the on-disk key segments use. Mirrors
    /// <c>GeoServicesCloudTileCache.SanitizeSegment</c>: lower-cased, with every character outside
    /// <c>[A-Za-z0-9-_.]</c> replaced by <c>'_'</c>, so a request value compares equal to the key
    /// segment the serve/write path produced from the same input.
    /// </summary>
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Embedded sprite/glyph assets served by the GeoServices VectorTileServer resources surface
// (honua-server#1780, epic #1776). Per the epic decision the sprite pipeline is scoped-minimal:
// Honua does not author per-service sprite sheets, so the sprite bytes are deterministic
// in-process stubs. Glyph ranges are real signed-distance-field glyphs rendered from one
// bundled sans-serif face (Resources/Glyphs, #5535): a requested fontstack is answered with
// that face when any of its fonts names a family the face substitutes for, and with the
// protocol's not-found answer otherwise — never with an empty glyph stack.

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Honua.Protocols.GeoServices.VectorTileServer.Services;

/// <summary>
/// Sprite/glyph assets served by the VectorTileServer <c>resources/sprites/*</c> and
/// <c>resources/fonts/*</c> routes. The sprite index is an empty JSON object and the sprite
/// image is a 1×1 fully transparent PNG; glyph ranges come from the bundled glyph resources and
/// are re-labelled with the requested fontstack.
/// </summary>
internal static class VectorTileEmbeddedAssets
{
    /// <summary>
    /// Honua's own name for the bundled glyph face; always served alongside the substituted
    /// family names in <see cref="SubstitutedFontFamilies"/>.
    /// </summary>
    internal const string DefaultFontStackName = "Honua Default";

    /// <summary>Content type for the sprite index JSON document.</summary>
    internal const string SpriteJsonContentType = "application/json";

    /// <summary>Content type for the sprite PNG image.</summary>
    internal const string SpritePngContentType = "image/png";

    /// <summary>Content type for the glyph protobuf payload (Mapbox glyphs encoding).</summary>
    internal const string GlyphPbfContentType = "application/x-protobuf";

    /// <summary>
    /// The empty sprite index document. An empty object is a valid Mapbox sprite index that
    /// declares zero icons, so a client that requested a sprite still parses a well-formed
    /// response.
    /// </summary>
    internal const string SpriteIndexJson = "{}";

    // 1x1 fully transparent 8-bit RGBA PNG (sig + IHDR + IDAT + IEND).
    private static readonly byte[] TransparentPngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0B, 0x49, 0x44, 0x41,
        0x54, 0x78, 0xDA, 0x63, 0x60, 0x00, 0x02, 0x00,
        0x00, 0x05, 0x00, 0x01, 0xE9, 0xFA, 0xDC, 0xD8,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44,
        0xAE, 0x42, 0x60, 0x82,
    ];

    // Bundled glyph ranges are embedded as "<prefix><range>.pbf" (see the project file).
    private const string GlyphResourcePrefix = "Honua.Protocols.GeoServices.VectorTileServer.Glyphs.";
    private const string GlyphResourceSuffix = ".pbf";

    // Protobuf field keys of the glyph encoding: glyphs.stacks (1), fontstack.name (1),
    // fontstack.range (2) and fontstack.glyphs (3), all length-delimited.
    private const byte StacksFieldKey = (1 << 3) | 2;
    private const byte NameFieldKey = (1 << 3) | 2;
    private const byte RangeFieldKey = (2 << 3) | 2;
    private const int GlyphsFieldNumber = 3;

    /// <summary>
    /// Sans-serif families the bundled face substitutes for. A font in a requested fontstack
    /// matches a family by its exact name or by the family name followed by a style suffix
    /// (for example <c>Arial Regular</c>, <c>Open Sans Bold</c>).
    /// </summary>
    private static readonly string[] SubstitutedFontFamilies =
    [
        DefaultFontStackName,
        "DejaVu Sans",
        "Arial",
        "Helvetica",
        "Open Sans",
        "Noto Sans",
        "Roboto",
        "Tahoma",
        "Verdana",
        "Segoe UI",
        "Calibri",
        "Liberation Sans",
        "Source Sans Pro",
        "Lato",
        "sans-serif",
    ];

    // The glyph records (encoded fontstack.glyphs entries) of every bundled range, keyed by the
    // range name and decoded on first use. Requests for other ranges never allocate an entry.
    private static readonly FrozenDictionary<string, Lazy<byte[]>> GlyphRecordsByRange =
        typeof(VectorTileEmbeddedAssets).Assembly.GetManifestResourceNames()
            .Where(static name => name.StartsWith(GlyphResourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(GlyphResourceSuffix, StringComparison.Ordinal))
            .ToFrozenDictionary(
                static name => name[GlyphResourcePrefix.Length..^GlyphResourceSuffix.Length],
                static name => new Lazy<byte[]>(() => LoadGlyphRecords(name)),
                StringComparer.Ordinal);

    /// <summary>Returns the 1×1 transparent sprite PNG bytes.</summary>
    internal static byte[] GetSpritePng() => (byte[])TransparentPngBytes.Clone();

    /// <summary>
    /// Builds the glyph PBF for <paramref name="fontstack"/> and <paramref name="range"/>: one
    /// fontstack named exactly as requested, carrying the bundled face's glyphs for the range.
    /// Returns <see langword="false"/> when no font in the fontstack is served or the range is
    /// not bundled, so the caller answers not-found instead of an empty glyph stack.
    /// </summary>
    internal static bool TryGetGlyphPbf(
        string fontstack,
        string range,
        [NotNullWhen(true)] out byte[]? glyphPbf)
    {
        glyphPbf = null;
        if (!IsServedFontStack(fontstack)
            || !GlyphRecordsByRange.TryGetValue(range, out var glyphRecords))
        {
            return false;
        }

        glyphPbf = EncodeGlyphStack(fontstack, range, glyphRecords.Value);
        return true;
    }

    /// <summary>
    /// Determines whether any font of the comma-separated <paramref name="fontstack"/> names a
    /// family the bundled face substitutes for.
    /// </summary>
    internal static bool IsServedFontStack(string fontstack)
    {
        if (string.IsNullOrWhiteSpace(fontstack))
        {
            return false;
        }

        foreach (var font in fontstack.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var family in SubstitutedFontFamilies)
            {
                if (font.StartsWith(family, StringComparison.OrdinalIgnoreCase)
                    && (font.Length == family.Length || font[family.Length] == ' '))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static byte[] EncodeGlyphStack(string fontstack, string range, byte[] glyphRecords)
    {
        var name = Encoding.UTF8.GetBytes(fontstack);
        var rangeBytes = Encoding.ASCII.GetBytes(range);
        var stackLength = 1 + VarintLength(name.Length) + name.Length
            + 1 + VarintLength(rangeBytes.Length) + rangeBytes.Length
            + glyphRecords.Length;

        var buffer = new byte[1 + VarintLength(stackLength) + stackLength];
        var offset = 0;
        buffer[offset++] = StacksFieldKey;
        WriteVarint(buffer, ref offset, stackLength);
        buffer[offset++] = NameFieldKey;
        WriteVarint(buffer, ref offset, name.Length);
        name.CopyTo(buffer, offset);
        offset += name.Length;
        buffer[offset++] = RangeFieldKey;
        WriteVarint(buffer, ref offset, rangeBytes.Length);
        rangeBytes.CopyTo(buffer, offset);
        offset += rangeBytes.Length;
        glyphRecords.CopyTo(buffer, offset);
        return buffer;
    }

    /// <summary>
    /// Reads a bundled glyph range and returns its encoded <c>fontstack.glyphs</c> entries
    /// (key, length and payload of each), dropping the bundled fontstack name and range so the
    /// response can carry the requested ones.
    /// </summary>
    private static byte[] LoadGlyphRecords(string resourceName)
    {
        using var stream = typeof(VectorTileEmbeddedAssets).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Glyph resource '{resourceName}' is not embedded.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var payload = memory.ToArray();

        var offset = 0;
        if (ReadVarint(payload, ref offset) != StacksFieldKey)
        {
            throw new InvalidDataException($"Glyph resource '{resourceName}' does not start with a fontstack.");
        }

        var stackEnd = checked((int)ReadVarint(payload, ref offset) + offset);
        using var records = new MemoryStream();
        while (offset < stackEnd)
        {
            var fieldStart = offset;
            var key = ReadVarint(payload, ref offset);
            if ((key & 7) != 2)
            {
                throw new InvalidDataException($"Glyph resource '{resourceName}' has an unexpected fontstack field.");
            }

            var length = checked((int)ReadVarint(payload, ref offset));
            offset += length;
            if (key >> 3 == GlyphsFieldNumber)
            {
                records.Write(payload, fieldStart, offset - fieldStart);
            }
        }

        return records.ToArray();
    }

    private static ulong ReadVarint(byte[] payload, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var current = payload[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }
    }

    private static void WriteVarint(byte[] buffer, ref int offset, int value)
    {
        var remaining = (uint)value;
        while (remaining >= 0x80)
        {
            buffer[offset++] = (byte)(remaining | 0x80);
            remaining >>= 7;
        }

        buffer[offset++] = (byte)remaining;
    }

    private static int VarintLength(int value)
    {
        var length = 1;
        for (var remaining = (uint)value; remaining >= 0x80; remaining >>= 7)
        {
            length++;
        }

        return length;
    }
}

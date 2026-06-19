// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Minimal embedded sprite/glyph assets served by the GeoServices VectorTileServer
// resources surface (honua-server#1780, epic #1776). Per the epic decision the sprite/glyph
// pipeline is scoped-minimal: Honua does not author per-service sprite sheets or glyph stacks,
// so symbol layers in a composed style resolve against these deterministic stubs rather than
// 404ing the client. The bytes are produced in-process (no embedded resource files) so the
// assembly stays AOT/trim-safe and the surface has no content-pipeline wiring.

namespace Honua.Protocols.GeoServices.VectorTileServer.Services;

/// <summary>
/// Deterministic, in-process sprite/glyph stub assets served by the VectorTileServer
/// <c>resources/sprites/*</c> and <c>resources/fonts/*</c> routes. The sprite index is an
/// empty JSON object and the sprite image is a 1×1 fully transparent PNG; the glyph stack is a
/// single minimal Mapbox glyph PBF whose lone fontstack carries the default name and range with
/// zero embedded glyphs.
/// </summary>
internal static class VectorTileEmbeddedAssets
{
    /// <summary>
    /// The fontstack name reported by the embedded glyph PBF. Composed styles that reference
    /// glyphs use the <c>{fontstack}</c>/<c>{range}</c> template, so the served fontstack name
    /// is informational; any requested fontstack resolves to this same minimal stack.
    /// </summary>
    internal const string DefaultFontStackName = "Honua Default";

    /// <summary>
    /// The single glyph range the minimal stack advertises (the canonical first 256-codepoint
    /// window). Out-of-range requests 404.
    /// </summary>
    internal const string DefaultGlyphRange = "0-255";

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

    // Minimal Mapbox glyph PBF: a `glyphs` message holding one `fontstack` with name and range
    // (field 1 = "Honua Default", field 2 = "0-255") and zero embedded glyphs.
    private static readonly byte[] GlyphStackBytes =
    [
        0x0A, 0x16, 0x0A, 0x0D, 0x48, 0x6F, 0x6E, 0x75,
        0x61, 0x20, 0x44, 0x65, 0x66, 0x61, 0x75, 0x6C,
        0x74, 0x12, 0x05, 0x30, 0x2D, 0x32, 0x35, 0x35,
    ];

    /// <summary>Returns the 1×1 transparent sprite PNG bytes.</summary>
    internal static byte[] GetSpritePng() => (byte[])TransparentPngBytes.Clone();

    /// <summary>Returns the minimal glyph stack PBF bytes.</summary>
    internal static byte[] GetGlyphPbf() => (byte[])GlyphStackBytes.Clone();

    /// <summary>
    /// Determines whether the requested glyph <paramref name="range"/> is the canonical
    /// 256-codepoint window the minimal stack serves. Only <c>0-255</c> is served; every other
    /// range 404s, matching the scoped-minimal decision.
    /// </summary>
    internal static bool IsServedRange(string range)
        => string.Equals(range, DefaultGlyphRange, StringComparison.Ordinal);
}

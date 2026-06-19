// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.VectorTileServer.Services;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VectorTileServer;

/// <summary>
/// Unit tests for <see cref="VectorTileStyleComposer"/> covering the sprite/glyphs scoping
/// rule introduced for honua-server#1780: sprite and glyphs references are emitted into the
/// composed Mapbox GL style ONLY when the style contains at least one <c>symbol</c> layer
/// (which is what consumes a sprite/glyph stack); otherwise they stay omitted.
/// </summary>
public sealed class VectorTileStyleComposerTests
{
    private const string ServiceName = "test";
    private const string SourceId = "esri";
    private const string TileUrl = "https://host/rest/services/test/VectorTileServer/tile/{z}/{y}/{x}.pbf";
    private const string SpriteUrl = "https://host/rest/services/test/VectorTileServer/resources/sprites/sprite";
    private const string GlyphsUrl = "https://host/rest/services/test/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf";

    [Fact]
    public void Compose_DefaultPointStyle_OmitsSpriteAndGlyphs()
    {
        var json = VectorTileStyleComposer.Compose(
            storedMapLibreJson: null,
            ServiceName,
            SourceId,
            TileUrl,
            MetadataV2GeometryType.Point,
            SpriteUrl,
            GlyphsUrl);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("version").GetInt32().Should().Be(8);
        root.TryGetProperty("sprite", out _).Should().BeFalse("the default point style has no symbol layers");
        root.TryGetProperty("glyphs", out _).Should().BeFalse("the default point style has no symbol layers");
    }

    [Fact]
    public void Compose_StoredStyleWithSymbolLayer_EmitsAbsoluteSpriteAndGlyphs()
    {
        const string storedStyle = """
            {
              "version": 8,
              "sources": {
                "esri": { "type": "vector", "tiles": ["https://old/tiles/{z}/{y}/{x}.pbf"] }
              },
              "layers": [
                {
                  "id": "labels",
                  "type": "symbol",
                  "source": "esri",
                  "source-layer": "layer",
                  "layout": { "text-field": "{name}", "text-font": ["Honua Default"] }
                }
              ]
            }
            """;

        var json = VectorTileStyleComposer.Compose(
            storedStyle,
            ServiceName,
            SourceId,
            TileUrl,
            MetadataV2GeometryType.Point,
            SpriteUrl,
            GlyphsUrl);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("sprite").GetString().Should().Be(SpriteUrl);
        root.GetProperty("glyphs").GetString().Should().Be(GlyphsUrl);

        // The vector source must still be rewritten onto this service's tile route.
        var tile = root.GetProperty("sources").GetProperty("esri")
            .GetProperty("tiles")[0].GetString();
        tile.Should().Be(TileUrl);
    }

    [Fact]
    public void Compose_StoredStyleWithSymbolLayer_StripsStaleStoredSpriteAndGlyphs()
    {
        // A stored style may carry sprite/glyphs pointers that do not resolve on this server;
        // the composer must replace them with this service's absolute references, never echo
        // the stale ones.
        const string storedStyle = """
            {
              "version": 8,
              "sprite": "https://stale/sprite",
              "glyphs": "https://stale/fonts/{fontstack}/{range}.pbf",
              "sources": { "esri": { "type": "vector", "url": "https://old/tilejson" } },
              "layers": [
                { "id": "labels", "type": "symbol", "source": "esri", "source-layer": "layer" }
              ]
            }
            """;

        var json = VectorTileStyleComposer.Compose(
            storedStyle,
            ServiceName,
            SourceId,
            TileUrl,
            MetadataV2GeometryType.Point,
            SpriteUrl,
            GlyphsUrl);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("sprite").GetString().Should().Be(SpriteUrl);
        root.GetProperty("glyphs").GetString().Should().Be(GlyphsUrl);
    }

    [Fact]
    public void Compose_StoredStyleWithoutSymbolLayer_OmitsSpriteAndGlyphs()
    {
        const string storedStyle = """
            {
              "version": 8,
              "sources": { "esri": { "type": "vector", "tiles": ["https://old/{z}/{y}/{x}.pbf"] } },
              "layers": [
                { "id": "fill", "type": "fill", "source": "esri", "source-layer": "layer" }
              ]
            }
            """;

        var json = VectorTileStyleComposer.Compose(
            storedStyle,
            ServiceName,
            SourceId,
            TileUrl,
            MetadataV2GeometryType.Polygon,
            SpriteUrl,
            GlyphsUrl);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.TryGetProperty("sprite", out _).Should().BeFalse();
        root.TryGetProperty("glyphs", out _).Should().BeFalse();
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.VectorTileServer.Services;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VectorTileServer;

public sealed class VectorTileStyleComposerTests
{
    private const string ServiceName = "test";
    private const string SourceId = "esri";
    private const string TileUrl = "https://host/rest/services/test/VectorTileServer/tile/{z}/{y}/{x}.pbf";
    private const string SpriteUrl = "https://host/rest/services/test/VectorTileServer/resources/sprites/sprite";
    private const string GlyphsUrl = "https://host/rest/services/test/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf";
    private const string ServiceUrl = "https://host/rest/services/test/VectorTileServer";

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public void Compose_DefaultPointStyle_OmitsSpriteAndGlyphs()
    {
        var json = ComposeWithSpriteAndGlyphs(null, MetadataV2GeometryType.Point);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("version").GetInt32().Should().Be(8);
        root.TryGetProperty("sprite", out _).Should().BeFalse("the default point style has no symbol layers");
        root.TryGetProperty("glyphs", out _).Should().BeFalse("the default point style has no symbol layers");
    }

    [global::Xunit.Trait("Tier", "Fast")]
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

        var json = ComposeWithSpriteAndGlyphs(storedStyle, MetadataV2GeometryType.Point);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("sprite").GetString().Should().Be(SpriteUrl);
        root.GetProperty("glyphs").GetString().Should().Be(GlyphsUrl);

        // The vector source must still be rewritten onto this service's tile route.
        var tile = root.GetProperty("sources").GetProperty("esri")
            .GetProperty("tiles")[0].GetString();
        tile.Should().Be(TileUrl);
    }

    [global::Xunit.Trait("Tier", "Fast")]
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

        var json = ComposeWithSpriteAndGlyphs(storedStyle, MetadataV2GeometryType.Point);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("sprite").GetString().Should().Be(SpriteUrl);
        root.GetProperty("glyphs").GetString().Should().Be(GlyphsUrl);
    }

    [global::Xunit.Trait("Tier", "Fast")]
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

        var json = ComposeWithSpriteAndGlyphs(storedStyle, MetadataV2GeometryType.Polygon);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.TryGetProperty("sprite", out _).Should().BeFalse();
        root.TryGetProperty("glyphs", out _).Should().BeFalse();
    }

    [UnitTest]
    public void Issue5534_DefaultStyle_VectorSourceReferencesTheServiceDescriptor()
    {
        var json = VectorTileStyleComposer.Compose(new VectorTileStyleOptions
        {
            StoredMapLibreJson = null,
            ServiceName = ServiceName,
            SourceId = SourceId,
            TileUrl = TileUrl,
            GeometryType = MetadataV2GeometryType.Point,
            SpriteUrl = SpriteUrl,
            GlyphsUrl = GlyphsUrl,
            ServiceUrl = ServiceUrl
        });

        using var document = JsonDocument.Parse(json);
        var source = document.RootElement.GetProperty("sources").GetProperty(SourceId);

        source.GetProperty("type").GetString().Should().Be("vector");
        source.GetProperty("url").GetString().Should().Be(ServiceUrl);
        source.GetProperty("tiles").EnumerateArray().Select(tile => tile.GetString())
            .Should().Equal(TileUrl);
    }

    [UnitTest]
    public void Issue5534_StoredStyle_EveryVectorSourceUrlIsReplacedWithTheServiceDescriptor()
    {
        const string storedStyle = """
            {
              "version": 8,
              "sources": {
                "esri": { "type": "vector", "url": "https://old/tilejson" },
                "other": { "type": "vector", "tiles": ["https://old/{z}/{y}/{x}.pbf"] }
              },
              "layers": [
                { "id": "fill", "type": "fill", "source": "esri", "source-layer": "layer" }
              ]
            }
            """;

        var json = VectorTileStyleComposer.Compose(new VectorTileStyleOptions
        {
            StoredMapLibreJson = storedStyle,
            ServiceName = ServiceName,
            SourceId = SourceId,
            TileUrl = TileUrl,
            GeometryType = MetadataV2GeometryType.Polygon,
            SpriteUrl = SpriteUrl,
            GlyphsUrl = GlyphsUrl,
            ServiceUrl = ServiceUrl
        });

        using var document = JsonDocument.Parse(json);
        foreach (var source in document.RootElement.GetProperty("sources").EnumerateObject())
        {
            source.Value.GetProperty("url").GetString().Should().Be(ServiceUrl, source.Name);
            source.Value.GetProperty("tiles").EnumerateArray().Select(tile => tile.GetString())
                .Should().Equal(TileUrl);
        }
    }

    [UnitTest]
    public void Compose_WithoutServiceUrl_DropsStoredSourceUrl()
    {
        const string storedStyle = """
            {
              "version": 8,
              "sources": { "esri": { "type": "vector", "url": "https://old/tilejson" } },
              "layers": [
                { "id": "fill", "type": "fill", "source": "esri", "source-layer": "layer" }
              ]
            }
            """;

        var json = VectorTileStyleComposer.Compose(new VectorTileStyleOptions
        {
            StoredMapLibreJson = storedStyle,
            ServiceName = ServiceName,
            SourceId = SourceId,
            TileUrl = TileUrl,
            GeometryType = MetadataV2GeometryType.Polygon
        });

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("sources").GetProperty(SourceId)
            .TryGetProperty("url", out _).Should().BeFalse();
    }

    [UnitTest]
    public void Compose_DefaultStyle_UsesDefaultTileZoomLimit()
    {
        var json = VectorTileStyleComposer.Compose(new VectorTileStyleOptions
        {
            StoredMapLibreJson = null,
            ServiceName = ServiceName,
            SourceId = SourceId,
            TileUrl = TileUrl,
            GeometryType = MetadataV2GeometryType.Point
        });

        using var document = JsonDocument.Parse(json);
        var source = document.RootElement.GetProperty("sources").GetProperty(SourceId);
        source.GetProperty("maxzoom").GetInt32().Should().Be(new TileLimits().MaxTileZoom);
    }

    [UnitTest]
    public void Compose_ConfiguredLimits_NarrowsEveryOwnedSourceAndPreservesTighterRanges()
    {
        const string storedStyle = """
            {
              "version": 8,
              "sources": {
                "wide": { "type": "vector", "minzoom": 0, "maxzoom": 22 },
                "implicit": { "type": "vector" },
                "narrow": { "type": "vector", "minzoom": 5, "maxzoom": 10 }
              },
              "layers": []
            }
            """;
        var json = VectorTileStyleComposer.Compose(new VectorTileStyleOptions
        {
            StoredMapLibreJson = storedStyle,
            ServiceName = ServiceName,
            SourceId = SourceId,
            TileUrl = TileUrl,
            GeometryType = MetadataV2GeometryType.Point,
            TileLimits = new TileLimits { MinTileZoom = 3, MaxTileZoom = 12 }
        });

        using var document = JsonDocument.Parse(json);
        var sources = document.RootElement.GetProperty("sources");
        foreach (var source in sources.EnumerateObject())
        {
            var narrow = source.Name == "narrow";
            source.Value.GetProperty("minzoom").GetInt32().Should().Be(narrow ? 5 : 3);
            source.Value.GetProperty("maxzoom").GetInt32().Should().Be(narrow ? 10 : 12);
            source.Value.GetProperty("tiles")[0].GetString().Should().Be(TileUrl);
        }
        sources.EnumerateObject().Should().HaveCount(4);
    }

    private static string ComposeWithSpriteAndGlyphs(string? storedStyle, MetadataV2GeometryType geometryType)
        => VectorTileStyleComposer.Compose(new VectorTileStyleOptions
        {
            StoredMapLibreJson = storedStyle,
            ServiceName = ServiceName,
            SourceId = SourceId,
            TileUrl = TileUrl,
            GeometryType = geometryType,
            SpriteUrl = SpriteUrl,
            GlyphsUrl = GlyphsUrl
        });
}

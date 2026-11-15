// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Infrastructure.Helpers;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Styling;

[Trait("Category", "Unit")]
public sealed class StyleEndpointZoomRangeTests
{
    private const string BaseUrl = "https://public.example.test";
    private static readonly TileLimits Limits = new() { MinTileZoom = 0, MaxTileZoom = 18 };

    [UnitTest]
    public void Resolve_OwnedSourceWiderThanTileRoute_NarrowsToTileRouteRange()
    {
        var sources = ResolveSources("""{"owned":{"type":"vector","tiles":["/tiles/7/{z}/{x}/{y}.mvt"],"minzoom":0,"maxzoom":22}}""", Limits);

        sources.GetProperty("owned").GetProperty("maxzoom").GetDouble().Should().Be(18);
        sources.GetProperty("owned").GetProperty("minzoom").GetDouble().Should().Be(0);
    }

    [UnitTest]
    public void Resolve_OwnedSourceWithoutMaxzoom_SetsTileRouteMaximum()
    {
        var sources = ResolveSources("""{"owned":{"type":"vector","tiles":["/tiles/7/{z}/{x}/{y}.mvt?token=source-owned"]}}""", Limits);

        sources.GetProperty("owned").GetProperty("maxzoom").GetDouble().Should().Be(18);
        sources.GetProperty("owned").TryGetProperty("minzoom", out _).Should().BeFalse();
    }

    [UnitTest]
    public void Resolve_AuthorNarrowerRange_KeepsRange()
    {
        var sources = ResolveSources("""{"owned":{"type":"vector","tiles":["/tiles/7/{z}/{x}/{y}.mvt"],"minzoom":4,"maxzoom":14}}""", Limits);

        sources.GetProperty("owned").GetProperty("maxzoom").GetDouble().Should().Be(14);
        sources.GetProperty("owned").GetProperty("minzoom").GetDouble().Should().Be(4);
    }

    [UnitTest]
    public void Resolve_ConfiguredMinimum_RaisesMinzoom()
    {
        var sources = ResolveSources("""{"owned":{"type":"vector","tiles":["/tiles/7/{z}/{x}/{y}.mvt"],"minzoom":0,"maxzoom":22}}""",
            new TileLimits { MinTileZoom = 2, MaxTileZoom = 16 });

        sources.GetProperty("owned").GetProperty("minzoom").GetDouble().Should().Be(2);
        sources.GetProperty("owned").GetProperty("maxzoom").GetDouble().Should().Be(16);
    }

    [UnitTest]
    public void Resolve_SourcesNotServedOnlyByLayerTileRoute_KeepRange()
    {
        // The H3 aggregate route is a different tile shape; only /tiles/{layerId}/{z}/{x}/{y}.mvt is narrowed.
        var sources = ResolveSources("""
            {"external":{"type":"vector","tiles":["https://other.example.test/tiles/7/{z}/{x}/{y}.mvt"],"maxzoom":22},
             "mixed":{"type":"vector","tiles":["/tiles/7/{z}/{x}/{y}.mvt","https://other.example.test/t/{z}/{x}/{y}.mvt"],"maxzoom":22},
             "tilejson":{"type":"vector","url":"/tiles/7/tile.json"},
             "h3":{"type":"vector","tiles":["/tiles/7/h3/{z}/{x}/{y}.mvt"],"maxzoom":22}}
            """, Limits);

        sources.GetProperty("external").GetProperty("maxzoom").GetDouble().Should().Be(22);
        sources.GetProperty("mixed").GetProperty("maxzoom").GetDouble().Should().Be(22);
        sources.GetProperty("tilejson").TryGetProperty("maxzoom", out _).Should().BeFalse();
        sources.GetProperty("h3").GetProperty("maxzoom").GetDouble().Should().Be(22);
    }

    [UnitTest]
    public void Resolve_AuthorRangeEntirelyAboveTileRoute_KeepsRangeInsteadOfInvertingIt()
    {
        var sources = ResolveSources("""{"owned":{"type":"vector","tiles":["/tiles/7/{z}/{x}/{y}.mvt"],"minzoom":19,"maxzoom":22}}""", Limits);

        sources.GetProperty("owned").GetProperty("minzoom").GetDouble().Should().Be(19);
        sources.GetProperty("owned").GetProperty("maxzoom").GetDouble().Should().Be(22);
    }

    [UnitTest]
    public void Resolve_NonNumericOrFractionalZoom_UsesTileRouteRange()
    {
        var sources = ResolveSources("""
            {"text":{"type":"vector","tiles":["/tiles/7/{z}/{x}/{y}.mvt"],"maxzoom":"22"},
             "empty":{"type":"vector","tiles":["/tiles/7/{z}/{x}/{y}.mvt"],"maxzoom":null},
             "fraction":{"type":"vector","tiles":["/tiles/7/{z}/{x}/{y}.mvt"],"maxzoom":18.5}}
            """, Limits);

        sources.GetProperty("text").GetProperty("maxzoom").GetDouble().Should().Be(18);
        sources.GetProperty("empty").GetProperty("maxzoom").GetDouble().Should().Be(18);
        sources.GetProperty("fraction").GetProperty("maxzoom").GetDouble().Should().Be(18);
    }

    [UnitTest]
    public void Resolve_RangeAlreadyWithinTileRoute_ReturnsInputUnchanged()
    {
        const string stored = """{"version":8,"sources":{"owned":{"type":"vector","tiles":["https://public.example.test/tiles/7/{z}/{x}/{y}.mvt"],"minzoom":0,"maxzoom":18}},"layers":[]}""";

        StyleEndpointUrlResolver.Resolve(stored, new StyleUrlContext(BaseUrl, null, Limits)).Should().BeSameAs(stored);
    }

    private static JsonElement ResolveSources(string sourcesJson, TileLimits limits)
    {
        using var input = JsonDocument.Parse($$"""{"version":8,"sources":{{sourcesJson}},"layers":[]}""");
        return StyleEndpointUrlResolver.Resolve(input.RootElement, new StyleUrlContext(BaseUrl, null, limits)).GetProperty("sources").Clone();
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Tiles;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Certification;

[Trait("Category", "Unit")]
public sealed class CertificationCriticalRegressionTests
{
    [UnitTest]
    public void Issue5566_OgcApiTilesDeclaresAndAcceptsTokenQueryParameter()
    {
        OgcTilesUtilities.AllowedQueryParameters.Metadata.Should().Contain("token");
        OgcTilesUtilities.AllowedQueryParameters.DatasetTilesetMetadata.Should().Contain("token");
        OgcTilesUtilities.AllowedQueryParameters.OpenApi.Should().Contain("token");
        OgcTilesUtilities.AllowedQueryParameters.Tiles.Should().Contain("token");
        OgcTilesUtilities.AllowedQueryParameters.DatasetTiles.Should().Contain("token");
    }

    [UnitTest]
    public void Issue5455_OgcApiFeaturesDeclaresAndAcceptsTokenQueryParameter()
    {
        OgcFeaturesUtilities.AllowedQueryParameters.Metadata.Should().Contain("token");
        OgcFeaturesUtilities.AllowedQueryParameters.Items.Should().Contain("token");
        OgcFeaturesUtilities.AllowedQueryParameters.Item.Should().Contain("token");
        OgcFeaturesUtilities.AllowedQueryParameters.OpenApi.Should().Contain("token");
        OgcFeaturesUtilities.AllowedQueryParameters.Transactions.Should().Contain("token");
        OgcFeaturesUtilities.AllowedQueryParameters.H3.Should().Contain("token");

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "openapi.json")));
        var scheme = document.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("PortalTokenQuery");

        scheme.GetProperty("type").GetString().Should().Be("apiKey");
        scheme.GetProperty("in").GetString().Should().Be("query");
        scheme.GetProperty("name").GetString().Should().Be("token");
    }

    [UnitTest]
    public void Issue5446_DatasetMapTilesDefaultWildcardRequestsToPng()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Accept = "*/*";

        OgcTilesUtilities.IsRasterTileFormat(null, context.Request, defaultToRaster: true)
            .Should().BeTrue();

        context.Request.Headers.Accept = "application/vnd.mapbox-vector-tile";
        OgcTilesUtilities.IsRasterTileFormat(null, context.Request, defaultToRaster: true)
            .Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("application/json")]
    [InlineData("image/png;q=0, application/vnd.mapbox-vector-tile;q=0")]
    public void Issue5446_DatasetMapTilesDoNotDefaultToPngWhenNoTileFormatIsAcceptable(string accept)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Accept = accept;

        // Neither branch accepts the request, so the handler's vector path answers 406.
        OgcTilesUtilities.IsRasterTileFormat(null, context.Request, defaultToRaster: true)
            .Should().BeFalse();
        OgcTilesUtilities.AcceptsVectorTiles(context.Request).Should().BeFalse();
    }

    [UnitTest]
    public void Issue5442_StyleDocumentEmitsAbsoluteEndpointUrls()
    {
        using var input = JsonDocument.Parse(
            """
            {
              "version": 8,
              "sprite": "/sprites/default",
              "glyphs": "/fonts/{fontstack}/{range}.pbf",
              "sources": {
                "layer-2110": {
                  "type": "vector",
                  "tiles": ["/tiles/2110/{z}/{x}/{y}.mvt"]
                }
              },
              "layers": []
            }
            """);

        var resolved = StyleEndpointUrlResolver.Resolve(input.RootElement, "https://public.example.test");

        resolved.GetProperty("sprite").GetString().Should().Be("https://public.example.test/sprites/default");
        resolved.GetProperty("glyphs").GetString().Should().Be("https://public.example.test/fonts/{fontstack}/{range}.pbf");
        resolved.GetProperty("sources")
            .GetProperty("layer-2110")
            .GetProperty("tiles")[0]
            .GetString().Should().Be("https://public.example.test/tiles/2110/{z}/{x}/{y}.mvt");
    }

    [UnitTest]
    public void Issue5442_CanonicalStylesheetContentEmitsAbsoluteEndpointUrls()
    {
        const string stored =
            """{"version":8,"sprite":"//cdn.example.test/sprites/default","sources":{"layer-2110":{"type":"vector","tiles":["/tiles/2110/{z}/{x}/{y}.mvt","https://other.example.test/t/{z}/{x}/{y}.mvt"]}},"layers":[]}""";

        using var resolved = JsonDocument.Parse(
            StyleEndpointUrlResolver.Resolve(stored, "https://public.example.test/"));

        var tiles = resolved.RootElement.GetProperty("sources").GetProperty("layer-2110").GetProperty("tiles");
        tiles[0].GetString().Should().Be("https://public.example.test/tiles/2110/{z}/{x}/{y}.mvt");
        tiles[1].GetString().Should().Be("https://other.example.test/t/{z}/{x}/{y}.mvt");
        resolved.RootElement.GetProperty("sprite").GetString().Should().Be("//cdn.example.test/sprites/default");
    }

    [UnitTest]
    public void Issue5442_CanonicalStylesheetContentWithoutRelativeUrlsIsReturnedUnchanged()
    {
        const string stored = """{ "version": 8, "sources": {}, "layers": [] }""";

        StyleEndpointUrlResolver.Resolve(stored, "https://public.example.test").Should().BeSameAs(stored);
    }
}

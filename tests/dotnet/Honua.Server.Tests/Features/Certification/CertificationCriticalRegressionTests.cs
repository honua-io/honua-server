// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Tiles;
using Honua.Server.Features.Styling;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Certification;

[Trait("Category", "Unit")]
public sealed class CertificationCriticalRegressionTests
{
    [Fact]
    public void Issue5455_OgcApiFeaturesDeclaresAndAcceptsTokenQueryParameter()
    {
        OgcFeaturesUtilities.AllowedQueryParameters.Metadata.Should().Contain("token");
        OgcFeaturesUtilities.AllowedQueryParameters.Items.Should().Contain("token");
        OgcFeaturesUtilities.AllowedQueryParameters.Item.Should().Contain("token");
        OgcFeaturesUtilities.AllowedQueryParameters.OpenApi.Should().Contain("token");
        OgcFeaturesUtilities.AllowedQueryParameters.Transactions.Should().Contain("token");
        OgcFeaturesUtilities.AllowedQueryParameters.H3.Should().Contain("token");

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "openapi.json")));
        var scheme = document.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("PortalTokenQuery");

        scheme.GetProperty("type").GetString().Should().Be("apiKey");
        scheme.GetProperty("in").GetString().Should().Be("query");
        scheme.GetProperty("name").GetString().Should().Be("token");
    }

    [Fact]
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

    [Fact]
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

        var resolved = StyleEndpoints.ResolveEndpointUrls(input.RootElement, "https://public.example.test");

        resolved.GetProperty("sprite").GetString().Should().Be("https://public.example.test/sprites/default");
        resolved.GetProperty("glyphs").GetString().Should().Be("https://public.example.test/fonts/{fontstack}/{range}.pbf");
        resolved.GetProperty("sources")
            .GetProperty("layer-2110")
            .GetProperty("tiles")[0]
            .GetString().Should().Be("https://public.example.test/tiles/2110/{z}/{x}/{y}.mvt");
    }
}

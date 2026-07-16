// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// Integration coverage for honua-server#2868: WMS GetMap must derive a MapLibre zoom from
/// BBOX + WIDTH/HEIGHT + CRS and apply each style layer's minzoom/maxzoom gate. Before the fix
/// GetMap passed no zoom at all, so every layer drew at every scale.
/// </summary>
/// <remarks>
/// Both requests below use the same EPSG:3857 envelope and differ only in image size, so the
/// geographic window (and therefore the features in view) is identical and the only variable is
/// the derived zoom. The envelope is the Web Mercator projection of -123,37 .. -122,38, which
/// contains the seeded point features. Derived zoom is min over the two axes of
/// log2(pixels / (512 * mercatorSpan)): 7.158 at 256px and 9.158 at 1024px, so a minzoom/maxzoom
/// of 8 falls cleanly between them with ~0.84 of margin on either side.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class OgcClassicWmsZoomGatingTests : IAsyncLifetime
{
    private const string Bbox3857 = "-13692297.3676,4439106.7873,-13580977.8768,4579425.8129";
    private const int ZoomedOutPixels = 256;   // derived MapLibre zoom ~7.158
    private const int ZoomedInPixels = 1024;   // derived MapLibre zoom ~9.158

    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static string CircleStyle(string? zoomScoping) =>
        $$"""
        {
          "version": 8,
          "sources": {},
          "layers": [
            {
              "id": "points",
              "type": "circle",
              "source": "features",
              {{zoomScoping}}
              "paint": { "circle-color": "#ff0000", "circle-radius": 10 }
            }
          ]
        }
        """;

    private async Task<byte[]> GetMapAsync(int pixels)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS" +
            $"?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX={Bbox3857}" +
            $"&WIDTH={pixels}&HEIGHT={pixels}&CRS=EPSG:3857" +
            $"&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TRANSPARENT=true");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(content));
        return content;
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_UnscopedStyle_RendersAtEveryScale()
    {
        var catalog = _fixture.GetService<ILayerStyleCatalog>();
        await catalog.SetMapLibreStyleAsync(WebAppFixture.TestLayerId, CircleStyle(null));

        // Baseline: with no minzoom/maxzoom the layer must draw at both scales. This pins the
        // style binding and the layer mapping so a gating failure below cannot be a false positive.
        HasStyledPixel(await GetMapAsync(ZoomedOutPixels)).Should().BeTrue();
        HasStyledPixel(await GetMapAsync(ZoomedInPixels)).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_MinZoomScopedStyle_OmitsLayerBelowThreshold()
    {
        var catalog = _fixture.GetService<ILayerStyleCatalog>();
        await catalog.SetMapLibreStyleAsync(WebAppFixture.TestLayerId, CircleStyle("\"minzoom\": 8,"));

        HasStyledPixel(await GetMapAsync(ZoomedOutPixels)).Should()
            .BeFalse("derived zoom ~7.16 is below the layer's minzoom of 8");
        HasStyledPixel(await GetMapAsync(ZoomedInPixels)).Should()
            .BeTrue("derived zoom ~9.16 is at or above the layer's minzoom of 8");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_MaxZoomScopedStyle_OmitsLayerAtOrAboveThreshold()
    {
        var catalog = _fixture.GetService<ILayerStyleCatalog>();
        await catalog.SetMapLibreStyleAsync(WebAppFixture.TestLayerId, CircleStyle("\"maxzoom\": 8,"));

        // maxzoom is exclusive in MapLibre, so the zoomed-in request (~9.16) is gated out while
        // the zoomed-out request (~7.16) still draws.
        HasStyledPixel(await GetMapAsync(ZoomedOutPixels)).Should()
            .BeTrue("derived zoom ~7.16 is below the layer's maxzoom of 8");
        HasStyledPixel(await GetMapAsync(ZoomedInPixels)).Should()
            .BeFalse("derived zoom ~9.16 is at or above the layer's exclusive maxzoom of 8");
    }

    private static bool HasStyledPixel(byte[] pngBytes)
    {
        using var bitmap = SKBitmap.Decode(pngBytes);
        bitmap.Should().NotBeNull("the GetMap response must be a decodable PNG");

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha > 0 && pixel.Red > 150 && pixel.Green < 80 && pixel.Blue < 80)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Honua.TestKit.Constants;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

/// <summary>
/// Integration coverage for honua-server#2498: the canonical <see cref="IRasterMapRenderer"/>
/// render path (the same path the MCP <c>honua_render_map</c> tool drives) must apply a
/// layer's bound vector style at pixel level on the default (Postgres) profile, and styled
/// rendering must no longer throw <see cref="NotSupportedException"/> for feature layers.
/// Layer 1 is a seeded vector (Point) collection with no raster coverage, so it exercises the
/// styled Skia fallback rather than the raster-coverage renderer.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiMaps)]
public sealed class StyledVectorMapRenderTests : IAsyncLifetime
{
    // Layer 1 is a pure-vector Point layer (seeded features 101-104 around -122.x, 37.x)
    // with no rows in raster_data, so the raster-coverage renderer yields no data and the
    // decorator falls back to the shared styled-vector pipeline.
    private const int VectorLayerId = 1;
    private const int TemporalVectorLayerId = 0;

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static MapRenderRequest BuildRequest() => new()
    {
        // Tight bbox around the layer-1 point features so the styled markers cover
        // several pixels and are trivially detectable.
        BoundingBox = [-123.0, 37.0, -122.0, 38.0],
        BoundingBoxCrs = 4326,
        Crs = 4326,
        Width = 256,
        Height = 256,
        Format = RasterFormat.PNG,
        Transparent = false
    };

    private static string CircleStyle(string hexColor) =>
        $$"""
        {
          "version": 8,
          "sources": {},
          "layers": [
            {
              "id": "points",
              "type": "circle",
              "source": "features",
              "paint": { "circle-color": "{{hexColor}}", "circle-radius": 10 }
            }
          ]
        }
        """;

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/map")]
    public async Task RenderDatasetMap_BoundVectorStyle_IsAppliedAtPixelLevelAndVariesByStyle()
    {
        var catalog = _fixture.GetService<ILayerStyleCatalog>();
        var renderer = _fixture.GetService<IRasterMapRenderer>();
        var request = BuildRequest();

        // Bind a red circle style and render through the canonical renderer.
        await catalog.SetMapLibreStyleAsync(VectorLayerId, CircleStyle("#ff0000"));
        var red = await renderer.RenderDatasetMapAsync([VectorLayerId], request);

        red.Data.Should().NotBeEmpty("a styled vector layer must render on the Postgres path (#2498)");
        HasPixel(red.Data, isRed: true).Should().BeTrue("the bound red circle style must be applied at pixel level");

        // Re-binding a different style must visibly change the rendered pixels, proving the
        // render reflects the bound style rather than a default symbology.
        await catalog.SetMapLibreStyleAsync(VectorLayerId, CircleStyle("#0000ff"));
        var blue = await renderer.RenderDatasetMapAsync([VectorLayerId], request);

        blue.Data.Should().NotBeEmpty();
        HasPixel(blue.Data, isRed: false).Should().BeTrue("the re-bound blue style must be applied at pixel level");
        HasPixel(blue.Data, isRed: true).Should().BeFalse("the previous red style must no longer be rendered");
        blue.Data.Should().NotEqual(red.Data, "styled output must differ when the bound style changes");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/styles/{styleId}/map")]
    public async Task RenderStyledMap_VectorLayer_NoLongerThrowsNotSupported_Issue2498Regression()
    {
        var catalog = _fixture.GetService<ILayerStyleCatalog>();
        var renderer = _fixture.GetService<IRasterMapRenderer>();
        var request = BuildRequest();

        await catalog.SetMapLibreStyleAsync(VectorLayerId, CircleStyle("#ff0000"));

        // Before the fix this threw NotSupportedException on the Postgres path; it must now
        // render the feature layer with its bound/style-resolved symbology.
        var styleId = "default";
        RasterResult result = default;
        var act = async () => result = await renderer.RenderStyledMapAsync(VectorLayerId, styleId, request);

        await act.Should().NotThrowAsync<NotSupportedException>(
            "styled rendering of a vector layer must be supported (#2498)");
        result.Data.Should().NotBeEmpty();
        HasPngSignature(result.Data).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_OutputCrs_TransformsBboxAndReportsRenderedCrs()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{VectorLayerId}/map" +
            "?bbox=-123,37,-122,38&bbox-crs=CRS84&crs=EPSG:3857&width=256&height=256&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Content-Crs", out var contentCrsValues).Should().BeTrue();
        contentCrsValues.Should().ContainSingle().Which.Should().Contain("3857");
        HasVisiblePixel(await response.Content.ReadAsByteArrayAsync()).Should().BeTrue(
            "the CRS84 bbox must be transformed before the Web Mercator render query (#4162)");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/styles/{styleId}/map")]
    public async Task GetStyledMap_OutputCrs_TransformsBboxAndReportsRenderedCrs()
    {
        var catalog = _fixture.GetService<ILayerStyleCatalog>();
        await catalog.SetMapLibreStyleAsync(VectorLayerId, CircleStyle("#ff0000"));
        var styleId = GetCollectionStyleId(VectorLayerId);

        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{VectorLayerId}/styles/{Uri.EscapeDataString(styleId)}/map" +
            "?bbox=-123,37,-122,38&bbox-crs=CRS84&crs=EPSG:3857&width=256&height=256&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Content-Crs", out var contentCrsValues).Should().BeTrue();
        contentCrsValues.Should().ContainSingle().Which.Should().Contain("3857");
        HasPixel(await response.Content.ReadAsByteArrayAsync(), isRed: true).Should().BeTrue(
            "styled rendering must transform the bbox and honor the requested output CRS (#4162)");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_Datetime_FiltersVectorFeatures()
    {
        var request =
            $"/ogc/maps/collections/{TemporalVectorLayerId}/map" +
            "?bbox=-123,37,-121,39&width=256&height=256&f=png";

        var unfiltered = await _fixture.Client.GetAsync(request);
        var filtered = await _fixture.Client.GetAsync(
            request + "&datetime=1900-01-01T00:00:00Z/1900-01-02T00:00:00Z");

        unfiltered.StatusCode.Should().Be(HttpStatusCode.OK);
        filtered.StatusCode.Should().Be(HttpStatusCode.OK);
        HasVisiblePixel(await unfiltered.Content.ReadAsByteArrayAsync()).Should().BeTrue();
        HasVisiblePixel(await filtered.Content.ReadAsByteArrayAsync()).Should().BeFalse(
            "the vector query must receive the advertised datetime filter (#4163)");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_TransparentAndBgcolor_AreRendered()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{VectorLayerId}/map" +
            "?bbox=-123,37,-122,38&width=256&height=256&f=png&transparent=false&bgcolor=0x00FF00");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var bitmap = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        bitmap.Should().NotBeNull();
        bitmap.GetPixel(0, 0).Should().Be(new SKColor(0, 255, 0, 255),
            "the documented opaque background color must reach the vector renderer (#4164)");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/styles/{styleId}/map")]
    public async Task GetStyledMap_UnknownStyle_ReturnsNotFound()
    {
        var catalog = _fixture.GetService<ILayerStyleCatalog>();
        await catalog.SetMapLibreStyleAsync(VectorLayerId, CircleStyle("#ff0000"));

        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{VectorLayerId}/styles/does-not-exist/map" +
            "?bbox=-123,37,-122,38&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an unknown style must not silently render the collection default (#4165)");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/styles/{styleId}/map")]
    public async Task GetStyledMap_UnassociatedStyle_ReturnsNotFound()
    {
        var catalog = _fixture.GetService<IStyleCatalog>();
        var styleId = $"unassociated-{Guid.NewGuid():N}";
        (await catalog.CreateStyleAsync(styleId, CircleStyle("#ff0000"))).Should().NotBeNull();

        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{VectorLayerId}/styles/{styleId}/map" +
            "?bbox=-123,37,-122,38&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a real style that is not associated with the collection must not be rendered (#4165)");
    }

    private string GetCollectionStyleId(int layerId)
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var resource).Should().BeTrue();
        resource.Should().NotBeNull();
        resource!.Metadata.Name.Should().NotBeNullOrWhiteSpace();
        return resource.Metadata.Name!;
    }

    private static bool HasPngSignature(byte[] data) =>
        data.Length > 8 &&
        data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    private static bool HasPixel(byte[] pngBytes, bool isRed)
    {
        using var bitmap = SKBitmap.Decode(pngBytes);
        bitmap.Should().NotBeNull("the render output must be a decodable image");

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (isRed)
                {
                    if (pixel.Red > 150 && pixel.Green < 80 && pixel.Blue < 80)
                    {
                        return true;
                    }
                }
                else if (pixel.Blue > 150 && pixel.Red < 80 && pixel.Green < 80)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasVisiblePixel(byte[] pngBytes)
    {
        using var bitmap = SKBitmap.Decode(pngBytes);
        bitmap.Should().NotBeNull("the render output must be a decodable image");

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

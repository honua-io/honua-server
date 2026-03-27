// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.TestKit.Attributes;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

/// <summary>
/// Tests for SkiaSharp-based map image rendering.
/// </summary>
[Trait("Component", "MapServer")]
public class SkiaMapRendererTests
{
    [UnitTest]
    public void RenderMap_EmptyFeatures_ReturnsValidPng()
    {
        using var renderer = new SkiaMapRenderer();
        var extent = new SkiaMapRenderer.RenderExtent(0, 0, 1, 1);

        var result = renderer.RenderMap(
            [],
            [],
            extent,
            256,
            256,
            transparent: true,
            backgroundColor: null,
            GeometryType.Point);

        result.Should().NotBeEmpty();
        // PNG magic bytes
        result[0].Should().Be(0x89);
        result[1].Should().Be(0x50); // 'P'
        result[2].Should().Be(0x4E); // 'N'
        result[3].Should().Be(0x47); // 'G'
    }

    [UnitTest]
    public void RenderMap_WithPointFeature_ReturnsValidPng()
    {
        using var renderer = new SkiaMapRenderer();
        var extent = new SkiaMapRenderer.RenderExtent(-1, -1, 1, 1);

        // Create a WKB point at (0, 0)
        var wkb = CreateWkbPoint(0, 0);
        var feature = new Feature
        {
            Id = 1,
            Geometry = wkb,
            Attributes = ImmutableDictionary<string, object?>.Empty
        };

        var result = renderer.RenderMap(
            [feature],
            [],
            extent,
            256,
            256,
            transparent: true,
            backgroundColor: null,
            GeometryType.Point);

        result.Should().NotBeEmpty();
        result[0].Should().Be(0x89); // PNG header
    }

    [UnitTest]
    public void RenderMap_OpaqueBackground_ReturnsValidPng()
    {
        using var renderer = new SkiaMapRenderer();
        var extent = new SkiaMapRenderer.RenderExtent(0, 0, 1, 1);

        var result = renderer.RenderMap(
            [],
            [],
            extent,
            100,
            100,
            transparent: false,
            backgroundColor: SKColors.White,
            GeometryType.None);

        result.Should().NotBeEmpty();
    }

    [UnitTest]
    public void RenderMap_WithStyleLayers_ReturnsValidPng()
    {
        using var renderer = new SkiaMapRenderer();
        var extent = new SkiaMapRenderer.RenderExtent(-1, -1, 1, 1);

        var wkb = CreateWkbPoint(0, 0);
        var feature = new Feature
        {
            Id = 1,
            Geometry = wkb,
            Attributes = ImmutableDictionary<string, object?>.Empty
        };

        var styleLayers = StyleTranslator.ParseStyleLayers(
            """[{"id":"circles","type":"circle","paint":{"circle-radius":5,"circle-color":"#ff0000"}}]""");

        var result = renderer.RenderMap(
            [feature],
            styleLayers,
            extent,
            256,
            256,
            transparent: true,
            backgroundColor: null,
            GeometryType.Point);

        result.Should().NotBeEmpty();
    }

    [UnitTest]
    public void RenderLegendSwatch_FillLayer_ReturnsValidPng()
    {
        var layer = new MapLibreStyleLayer { Type = "fill" };

        var result = SkiaMapRenderer.RenderLegendSwatch(layer, GeometryType.Polygon);

        result.Should().NotBeEmpty();
        result[0].Should().Be(0x89); // PNG header
    }

    [UnitTest]
    public void RenderLegendSwatch_LineLayer_ReturnsValidPng()
    {
        var layers = StyleTranslator.ParseStyleLayers(
            """[{"id":"l","type":"line","paint":{"line-color":"#0000ff","line-width":2}}]""");

        var result = SkiaMapRenderer.RenderLegendSwatch(layers[0], GeometryType.LineString);

        result.Should().NotBeEmpty();
    }

    [UnitTest]
    public void RenderLegendSwatch_CircleLayer_ReturnsValidPng()
    {
        var layers = StyleTranslator.ParseStyleLayers(
            """[{"id":"c","type":"circle","paint":{"circle-radius":5,"circle-color":"#00ff00"}}]""");

        var result = SkiaMapRenderer.RenderLegendSwatch(layers[0], GeometryType.Point);

        result.Should().NotBeEmpty();
    }

    [UnitTest]
    public void RenderLegendSwatch_DefaultLayer_ReturnsValidPng()
    {
        var layer = new MapLibreStyleLayer { Type = "unknown" };

        var result = SkiaMapRenderer.RenderLegendSwatch(layer, GeometryType.Polygon);

        result.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RenderMap_NonPositiveWidth_ThrowsArgumentOutOfRangeException(int width)
    {
        using var renderer = new SkiaMapRenderer();
        var extent = new SkiaMapRenderer.RenderExtent(0, 0, 1, 1);

        var act = () => renderer.RenderMap([], [], extent, width, 10, true, null, GeometryType.None);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("imageWidth");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RenderLegendSwatch_NonPositiveHeight_ThrowsArgumentOutOfRangeException(int height)
    {
        var layer = new MapLibreStyleLayer { Type = "fill" };

        var act = () => SkiaMapRenderer.RenderLegendSwatch(layer, GeometryType.Polygon, width: 20, height: height);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("height");
    }

    [UnitTest]
    public void BuildTransform_ValidExtent_TransformsCorrectly()
    {
        var extent = new SkiaMapRenderer.RenderExtent(0, 0, 100, 100);

        var transform = SkiaMapRenderer.BuildTransform(extent, 200, 200);

        // Center of extent should map to center of image
        var center = transform(50, 50);
        center.X.Should().BeApproximately(100f, 1f);
        center.Y.Should().BeApproximately(100f, 1f);
    }

    [UnitTest]
    public void BuildTransform_ZeroExtent_ReturnsCenterPoint()
    {
        var extent = new SkiaMapRenderer.RenderExtent(5, 5, 5, 5);

        var transform = SkiaMapRenderer.BuildTransform(extent, 200, 200);

        var point = transform(5, 5);
        point.X.Should().Be(100f);
        point.Y.Should().Be(100f);
    }

    [UnitTest]
    public void EncodeSurface_Png_ReturnsPngBytes()
    {
        using var surface = SKSurface.Create(new SKImageInfo(10, 10));
        surface.Canvas.Clear(SKColors.Red);

        var result = SkiaMapRenderer.EncodeSurface(surface, "png");

        result.Should().NotBeEmpty();
        result[0].Should().Be(0x89);
    }

    [UnitTest]
    public void EncodeSurface_Jpeg_ReturnsJpegBytes()
    {
        using var surface = SKSurface.Create(new SKImageInfo(10, 10));
        surface.Canvas.Clear(SKColors.Red);

        var result = SkiaMapRenderer.EncodeSurface(surface, "jpg");

        result.Should().NotBeEmpty();
        // JPEG magic bytes: FF D8
        result[0].Should().Be(0xFF);
        result[1].Should().Be(0xD8);
    }

    [UnitTest]
    public void GetContentType_Png_ReturnsCorrectType()
    {
        SkiaMapRenderer.GetContentType("png").Should().Be("image/png");
    }

    [UnitTest]
    public void GetContentType_Jpeg_ReturnsCorrectType()
    {
        SkiaMapRenderer.GetContentType("jpg").Should().Be("image/jpeg");
        SkiaMapRenderer.GetContentType("jpeg").Should().Be("image/jpeg");
    }

    [UnitTest]
    public void GetContentType_Gif_ReturnsCorrectType()
    {
        SkiaMapRenderer.GetContentType("gif").Should().Be("image/gif");
    }

    [UnitTest]
    public void GetContentType_Unknown_ReturnsPng()
    {
        SkiaMapRenderer.GetContentType("bmp").Should().Be("image/png");
    }

    [UnitTest]
    public void RenderExtent_Width_IsCorrect()
    {
        var extent = new SkiaMapRenderer.RenderExtent(10, 20, 30, 50);

        extent.Width.Should().Be(20);
        extent.Height.Should().Be(30);
    }

    [UnitTest]
    public void Dispose_PreventsReuse()
    {
        var renderer = new SkiaMapRenderer();
        renderer.Dispose();

        var extent = new SkiaMapRenderer.RenderExtent(0, 0, 1, 1);
        var act = () => renderer.RenderMap([], [], extent, 10, 10, true, null, GeometryType.None);

        act.Should().Throw<ObjectDisposedException>();
    }

    /// <summary>
    /// Creates a simple WKB Point geometry.
    /// </summary>
    private static byte[] CreateWkbPoint(double x, double y)
    {
        var wkb = new byte[21];
        wkb[0] = 1; // little-endian
        BitConverter.TryWriteBytes(wkb.AsSpan(1), 1); // Point type
        BitConverter.TryWriteBytes(wkb.AsSpan(5), x);
        BitConverter.TryWriteBytes(wkb.AsSpan(13), y);
        return wkb;
    }
}

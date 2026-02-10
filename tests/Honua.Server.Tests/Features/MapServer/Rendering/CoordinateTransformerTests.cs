// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.MapServer.Rendering;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.MapServer.Rendering;

/// <summary>
/// Tests for coordinate transformation between geographic and pixel space.
/// </summary>
[Trait("Component", "MapServer")]
public class CoordinateTransformerTests
{
    [UnitTest]
    public void TransformPoint_SameSrid_ReturnsOriginal()
    {
        var (x, y) = CoordinateTransformer.TransformPoint(10.0, 20.0, 4326, 4326);

        x.Should().Be(10.0);
        y.Should().Be(20.0);
    }

    [UnitTest]
    public void TransformPoint_4326To3857_TransformsCorrectly()
    {
        // London: ~0, ~51.5
        var (x, y) = CoordinateTransformer.TransformPoint(0.0, 51.5, 4326, 3857);

        x.Should().BeApproximately(0.0, 1.0);
        y.Should().BeApproximately(6_711_455.0, 10_000.0);
    }

    [UnitTest]
    public void TransformPoint_3857To4326_TransformsCorrectly()
    {
        var (x, y) = CoordinateTransformer.TransformPoint(0.0, 6_711_455.0, 3857, 4326);

        x.Should().BeApproximately(0.0, 0.001);
        y.Should().BeApproximately(51.5, 0.1);
    }

    [UnitTest]
    public void TransformPoint_4326To3857_RoundTrip_IsConsistent()
    {
        var lon = -122.4194;
        var lat = 37.7749;

        var (mx, my) = CoordinateTransformer.LonLatToWebMercator(lon, lat);
        var (backLon, backLat) = CoordinateTransformer.WebMercatorToLonLat(mx, my);

        backLon.Should().BeApproximately(lon, 0.0001);
        backLat.Should().BeApproximately(lat, 0.0001);
    }

    [UnitTest]
    public void TransformExtent_SameSrid_ReturnsOriginal()
    {
        var extent = new SkiaMapRenderer.RenderExtent(-180, -90, 180, 90);

        var result = CoordinateTransformer.TransformExtent(extent, 4326, 4326);

        result.MinX.Should().Be(-180);
        result.MinY.Should().Be(-90);
        result.MaxX.Should().Be(180);
        result.MaxY.Should().Be(90);
    }

    [UnitTest]
    public void TransformExtent_4326To3857_TransformsCorners()
    {
        var extent = new SkiaMapRenderer.RenderExtent(-10, -10, 10, 10);

        var result = CoordinateTransformer.TransformExtent(extent, 4326, 3857);

        result.MinX.Should().BeLessThan(0);
        result.MaxX.Should().BeGreaterThan(0);
        result.MinY.Should().BeLessThan(0);
        result.MaxY.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public void CalculateScaleDenominator_ValidInput_ReturnsPositive()
    {
        var extent = new SkiaMapRenderer.RenderExtent(-1, -1, 1, 1);

        var scale = CoordinateTransformer.CalculateScaleDenominator(extent, 256, 96, 4326);

        scale.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public void CalculateScaleDenominator_ZeroWidth_ReturnsZero()
    {
        var extent = new SkiaMapRenderer.RenderExtent(0, 0, 1, 1);

        var scale = CoordinateTransformer.CalculateScaleDenominator(extent, 0, 96, 4326);

        scale.Should().Be(0);
    }

    [UnitTest]
    public void PixelToMapUnits_ValidInput_ReturnsCorrectUnits()
    {
        var extent = new SkiaMapRenderer.RenderExtent(0, 0, 100, 100);

        var units = CoordinateTransformer.PixelToMapUnits(5, extent, 500);

        units.Should().BeApproximately(1.0, 0.001);
    }

    [UnitTest]
    public void PixelToMapUnits_ZeroImageWidth_ReturnsZero()
    {
        var extent = new SkiaMapRenderer.RenderExtent(0, 0, 100, 100);

        var units = CoordinateTransformer.PixelToMapUnits(5, extent, 0);

        units.Should().Be(0);
    }

    [UnitTest]
    public void LonLatToWebMercator_ClampsLatitude()
    {
        // Beyond 85.06 should be clamped
        var (_, y1) = CoordinateTransformer.LonLatToWebMercator(0, 90);
        var (_, y2) = CoordinateTransformer.LonLatToWebMercator(0, 85.06);

        y1.Should().Be(y2);
    }
}

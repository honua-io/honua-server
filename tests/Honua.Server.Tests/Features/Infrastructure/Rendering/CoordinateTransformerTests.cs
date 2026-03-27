// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

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
    public void TransformPoint_WebMercatorAliasTo4326_TransformsCorrectly()
    {
        var (x, y) = CoordinateTransformer.TransformPoint(0.0, 6_711_455.0, 102100, 4326);

        x.Should().BeApproximately(0.0, 0.001);
        y.Should().BeApproximately(51.5, 0.1);
    }

    [UnitTest]
    public void TransformPoint_4326ToWebMercatorAlias_TransformsCorrectly()
    {
        var (x, y) = CoordinateTransformer.TransformPoint(0.0, 51.5, 4326, 102100);

        x.Should().BeApproximately(0.0, 1.0);
        y.Should().BeApproximately(6_711_455.0, 10_000.0);
    }

    [UnitTest]
    public void TransformPoint_GeographicSridAliasTo4326_ThrowsNotSupportedException()
    {
        var act = () => CoordinateTransformer.TransformPoint(-122.5, 37.5, 4269, 4326);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*SRID 4269 to 4326*");
    }

    [UnitTest]
    public void TransformExtent_GeographicSridAliasTo4326_ThrowsNotSupportedException()
    {
        var extent = new SkiaMapRenderer.RenderExtent(-123, 37, -122, 38);

        var act = () => CoordinateTransformer.TransformExtent(extent, 4269, 4326);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*SRID 4269 to 4326*");
    }

    [UnitTest]
    public void TransformPoint_GeographicSridAliasTo3857_ThrowsNotSupportedException()
    {
        var act = () => CoordinateTransformer.TransformPoint(-122.5, 37.5, 4269, 3857);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*SRID 4269 to 3857*");
    }

    [UnitTest]
    public void TransformPoint_3857ToGeographicSridAlias_ThrowsNotSupportedException()
    {
        var act = () => CoordinateTransformer.TransformPoint(-13636637, 4509031, 3857, 4269);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*SRID 3857 to 4269*");
    }

    [UnitTest]
    public void TransformExtent_GeographicSridAliasTo3857_ThrowsNotSupportedException()
    {
        var extent = new SkiaMapRenderer.RenderExtent(-123, 37, -122, 38);

        var act = () => CoordinateTransformer.TransformExtent(extent, 4269, 3857);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*SRID 4269 to 3857*");
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
    public void CalculateScaleDenominator_FootBasedSrid_ScalesCorrectly()
    {
        // EPSG 2229 = NAD83 / California zone 5 (ftUS).
        // Same extent in feet vs meters should produce a ~3.28x smaller scale denominator
        // because feet are smaller units, so the extent covers fewer meters.
        var extentFeet = new SkiaMapRenderer.RenderExtent(0, 0, 10_000, 10_000);

        var scaleFeet = CoordinateTransformer.CalculateScaleDenominator(extentFeet, 256, 96, 2229);
        var scaleMeters = CoordinateTransformer.CalculateScaleDenominator(extentFeet, 256, 96, 3857);

        // The foot-based scale should be roughly 0.3048x the meter-based scale
        var ratio = scaleFeet / scaleMeters;
        ratio.Should().BeApproximately(1200.0 / 3937.0, 0.001);
    }

    [UnitTest]
    public void LinearUnitToMeters_MeterSrid_ReturnsOne()
    {
        CoordinateTransformer.LinearUnitToMeters(3857).Should().Be(1.0);
        CoordinateTransformer.LinearUnitToMeters(32617).Should().Be(1.0);
    }

    [UnitTest]
    public void LinearUnitToMeters_UsSurveyFootSrid_ReturnsConversionFactor()
    {
        var expected = 1200.0 / 3937.0;
        CoordinateTransformer.LinearUnitToMeters(2229).Should().Be(expected);
        CoordinateTransformer.LinearUnitToMeters(2965).Should().Be(expected);
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

    [UnitTest]
    public void AdjustExtentForScale_RoundTripsWidth_Geographic()
    {
        // For geographic CRS, scale is derived from width; X coordinates should round-trip
        var original = new SkiaMapRenderer.RenderExtent(-122.5, 37.0, -122.0, 37.5);
        int imageWidth = 800, imageHeight = 600, dpi = 96, srid = 4326;

        var scale = CoordinateTransformer.CalculateScaleDenominator(original, imageWidth, dpi, srid);
        var adjusted = CoordinateTransformer.AdjustExtentForScale(original, scale, imageWidth, imageHeight, dpi, srid);

        adjusted.MinX.Should().BeApproximately(original.MinX, 1e-6);
        adjusted.MaxX.Should().BeApproximately(original.MaxX, 1e-6);
    }

    [UnitTest]
    public void AdjustExtentForScale_RoundTrips_Projected()
    {
        // For projected CRS with matching aspect ratios, full round-trip works
        var original = new SkiaMapRenderer.RenderExtent(0, 0, 800, 600);
        int imageWidth = 800, imageHeight = 600, dpi = 96, srid = 3857;

        var scale = CoordinateTransformer.CalculateScaleDenominator(original, imageWidth, dpi, srid);
        var adjusted = CoordinateTransformer.AdjustExtentForScale(original, scale, imageWidth, imageHeight, dpi, srid);

        adjusted.MinX.Should().BeApproximately(original.MinX, 1e-4);
        adjusted.MaxX.Should().BeApproximately(original.MaxX, 1e-4);
        adjusted.MinY.Should().BeApproximately(original.MinY, 1e-4);
        adjusted.MaxY.Should().BeApproximately(original.MaxY, 1e-4);
    }

    [UnitTest]
    public void AdjustExtentForScale_LargerScale_ExpandsExtent()
    {
        var original = new SkiaMapRenderer.RenderExtent(-1, -1, 1, 1);
        int imageWidth = 256, imageHeight = 256, dpi = 96, srid = 3857;

        var originalScale = CoordinateTransformer.CalculateScaleDenominator(original, imageWidth, dpi, srid);
        var adjusted = CoordinateTransformer.AdjustExtentForScale(original, originalScale * 2, imageWidth, imageHeight, dpi, srid);

        adjusted.Width.Should().BeApproximately(original.Width * 2, 0.01);
    }

    [UnitTest]
    public void AdjustExtentForScale_PreservesCenter()
    {
        var original = new SkiaMapRenderer.RenderExtent(100, 200, 300, 500);
        var adjusted = CoordinateTransformer.AdjustExtentForScale(original, 50000, 800, 600, 96, 3857);

        var originalCenterX = (original.MinX + original.MaxX) / 2.0;
        var originalCenterY = (original.MinY + original.MaxY) / 2.0;
        var adjustedCenterX = (adjusted.MinX + adjusted.MaxX) / 2.0;
        var adjustedCenterY = (adjusted.MinY + adjusted.MaxY) / 2.0;

        adjustedCenterX.Should().BeApproximately(originalCenterX, 1e-6);
        adjustedCenterY.Should().BeApproximately(originalCenterY, 1e-6);
    }

    [UnitTest]
    public void AdjustExtentForScale_ZeroScale_ReturnsOriginal()
    {
        var original = new SkiaMapRenderer.RenderExtent(-1, -1, 1, 1);
        var adjusted = CoordinateTransformer.AdjustExtentForScale(original, 0, 256, 256, 96, 4326);

        adjusted.Should().Be(original);
    }
}

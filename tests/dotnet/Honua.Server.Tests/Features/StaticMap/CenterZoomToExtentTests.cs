// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Server.Features.StaticMap;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.StaticMap;

[Protocol(TestProtocols.StaticMap)]
public sealed class CenterZoomToExtentTests
{
    private const double MercatorMaxLat = SpatialConstants.WebMercatorMaxLatitude; // 85.0511…

    [UnitTest]
    [Operation(Operations.Render)]
    public void Equator_Zoom0_CoversFullExtent()
    {
        // At zoom 0, a 256px image spans the full Web Mercator world
        var extent = StaticMapEndpoints.CenterZoomToExtent(0, 0, 0, 256, 256);

        extent.MinX.Should().BeApproximately(-180, 0.01);
        extent.MaxX.Should().BeApproximately(180, 0.01);
        // Y must clamp to the Web Mercator latitude limit, not ±90
        extent.MinY.Should().BeApproximately(-MercatorMaxLat, 0.01);
        extent.MaxY.Should().BeApproximately(MercatorMaxLat, 0.01);
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public void Equator_HighZoom_ProducesSmallExtent()
    {
        var extent = StaticMapEndpoints.CenterZoomToExtent(0, 0, 18, 256, 256);

        extent.Width.Should().BeLessThan(0.01, "zoom 18 should produce a very small extent");
        extent.Height.Should().BeLessThan(0.01);
        // Symmetric around center
        extent.MinX.Should().BeApproximately(-extent.MaxX, 1e-10);
        extent.MinY.Should().BeApproximately(-extent.MaxY, 1e-10);
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public void HighLatitude_YExtentShrinks()
    {
        // Mercator Y derivative at latitude φ is R/cos(φ), so for small extents
        // the geographic height scales as cos(φ). At 60°N → ~half of equator.
        var equator = StaticMapEndpoints.CenterZoomToExtent(0, 0, 10, 256, 256);
        var lat60 = StaticMapEndpoints.CenterZoomToExtent(0, 60, 10, 256, 256);

        lat60.Height.Should().BeApproximately(equator.Height * 0.5, equator.Height * 0.02,
            "Y extent at 60°N should be ~half the equator extent due to Mercator scaling");
        // X extent is latitude-independent in Web Mercator
        lat60.Width.Should().BeApproximately(equator.Width, 1e-10);
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public void NearPole_ExtentClampsToMercatorBounds()
    {
        var extent = StaticMapEndpoints.CenterZoomToExtent(0, 85, 2, 512, 512);

        // Clamped by Web Mercator bounds, not ±90
        extent.MaxY.Should().BeLessOrEqualTo(MercatorMaxLat + 0.001);
        extent.MinY.Should().BeGreaterOrEqualTo(-MercatorMaxLat - 0.001);
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public void NearAntimeridian_ExtentClampsToValidBounds()
    {
        // Center near 180° with wide extent that would overflow
        var extent = StaticMapEndpoints.CenterZoomToExtent(179, 0, 2, 512, 512);

        extent.MaxX.Should().BeLessOrEqualTo(180);
        extent.MinX.Should().BeGreaterOrEqualTo(-180);
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public void NonSquareDimensions_ProducesAsymmetricExtent()
    {
        // At the equator the Mercator projection is locally conformal,
        // so an 800×400 image should produce width ≈ 2× height.
        var extent = StaticMapEndpoints.CenterZoomToExtent(0, 0, 10, 800, 400);

        extent.Width.Should().BeApproximately(extent.Height * 2, extent.Height * 0.01);
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public void MaxZoom_ProducesValidExtent()
    {
        var extent = StaticMapEndpoints.CenterZoomToExtent(-122.4, 37.7, 22, 256, 256);

        extent.Width.Should().BeGreaterThan(0);
        extent.Height.Should().BeGreaterThan(0);
        extent.MinX.Should().BeLessThan(extent.MaxX);
        extent.MinY.Should().BeLessThan(extent.MaxY);
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public void CenterZoom_MercatorTransform_PlacesCenterCorrectlyAtHighLatitude()
    {
        // At 60°N / zoom 2, CenterZoomToExtent produces a geographic extent whose
        // midpoint is NOT at 60°N (Mercator inverse-projection is non-linear).
        // Rendering in Mercator (3857) must place the center at the image center;
        // a linear geographic (4326) transform would mis-place it by ~89 px.
        const int width = 400;
        const int height = 400;
        var geoExtent = StaticMapEndpoints.CenterZoomToExtent(0, 60, 2, width, height);

        // Mercator transform (correct for center/zoom rendering)
        var mercExtent = CoordinateTransformer.TransformExtent(geoExtent, 4326, 3857);
        var mercTransform = SkiaMapRenderer.BuildTransform(mercExtent, width, height);
        var (cx, cy) = CoordinateTransformer.LonLatToWebMercator(0, 60);
        var mercCenter = mercTransform(cx, cy);

        mercCenter.X.Should().BeApproximately(width / 2f, 1f,
            "center lon should map to horizontal center in Mercator");
        mercCenter.Y.Should().BeApproximately(height / 2f, 1f,
            "center lat should map to vertical center in Mercator");

        // Linear geographic transform (the old, incorrect behavior)
        var geoTransform = SkiaMapRenderer.BuildTransform(geoExtent, width, height);
        var geoCenter = geoTransform(0, 60);

        // The geographic transform places the center far from pixel 200 due to
        // the non-linear relationship between geographic latitude and Mercator y.
        var geoError = Math.Abs(geoCenter.Y - height / 2f);
        geoError.Should().BeGreaterThan(50,
            "linear geographic transform should visibly mis-place center at 60°N");
    }
}

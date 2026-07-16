// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Rendering;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

/// <summary>
/// Tests for deriving a MapLibre zoom level from a render envelope and image size.
/// </summary>
/// <remarks>
/// The reference envelopes below are the bounds a MapLibre GL JS map reports for a known
/// camera zoom, center, and viewport at bearing 0 / pitch 0. They are computed from MapLibre's
/// own published definitions: <c>mercatorXfromLng</c>/<c>mercatorYfromLat</c>
/// (src/geo/mercator_coordinate.ts) over a world of <c>tileSize * 2^zoom</c> pixels, where
/// <c>Transform.tileSize</c> is the constant 512 (src/geo/transform_helper.ts). Deriving the zoom
/// back from those bounds must return the camera zoom MapLibre started from — a derivation that is
/// off by one silently drops or adds a layer.
/// </remarks>
[Trait("Component", "MapServer")]
public class RenderZoomTests
{
    private const int WebMercator = 3857;
    private const int Wgs84 = 4326;
    private const double WorldSpanMeters = 40075016.685578488;

    [Theory]
    // center lng/lat, camera zoom, viewport px -> the EPSG:3857 bounds MapLibre covers.
    [InlineData("SF", -13637449.2108385768, 4540337.3996251794, -13617881.3315975722, 4555013.3090559337, 1024, 768, 12.0)]
    [InlineData("Oslo", 1195018.5032003040, 8379160.3756668363, 1198840.3546145628, 8382026.7642275300, 800, 600, 14.0)]
    [InlineData("Sydney", 16793406.5207253322, -4050334.4057895844, 16871678.0376893505, -3972062.8888255637, 512, 512, 9.0)]
    [InlineData("NullIsland", -20037508.3427892439, -20037508.3427892439, 20037508.3427892439, 20037508.3427892439, 512, 512, 0.0)]
    [InlineData("Quito", -8735377.7248098571, -20421.2134459828, -8734613.3545270059, -19809.7172197014, 1280, 1024, 17.0)]
    public void FromWebMercatorExtent_MapLibreViewport_RecoversCameraZoom(
        string label,
        double minX,
        double minY,
        double maxX,
        double maxY,
        int imageWidth,
        int imageHeight,
        double expectedZoom)
    {
        label.Should().NotBeNull();
        var extent = new SkiaMapRenderer.RenderExtent(minX, minY, maxX, maxY);

        var zoom = RenderZoom.FromWebMercatorExtent(extent, imageWidth, imageHeight);

        zoom.Level.Should().NotBeNull();
        zoom.Level!.Value.Should().BeApproximately(expectedZoom, 1e-9);
        zoom.NotDerivableReason.Should().BeNull();
    }

    [UnitTest]
    public void FromExtent_Wgs84WholeWorld_MatchesWebMercatorWholeWorld()
    {
        var geographic = RenderZoom.FromExtent(
            new SkiaMapRenderer.RenderExtent(-180, -85.0511287798066, 180, 85.0511287798066),
            512,
            512,
            Wgs84);

        geographic.Level.Should().NotBeNull();
        geographic.Level!.Value.Should().BeApproximately(0.0, 1e-9);
    }

    [Theory]
    // A whole-world Web Mercator envelope is MapLibre zoom 0 at 512px, because MapLibre's world
    // is 512 * 2^zoom pixels wide. Halving the pixels halves the detail: one zoom level down.
    [InlineData(512, 0.0)]
    [InlineData(256, -1.0)]
    [InlineData(1024, 1.0)]
    [InlineData(2048, 2.0)]
    public void FromWebMercatorExtent_WholeWorld_ScalesWithImageSize(int imagePixels, double expectedZoom)
    {
        var half = WorldSpanMeters / 2.0;
        var extent = new SkiaMapRenderer.RenderExtent(-half, -half, half, half);

        var zoom = RenderZoom.FromWebMercatorExtent(extent, imagePixels, imagePixels);

        zoom.Level!.Value.Should().BeApproximately(expectedZoom, 1e-9);
    }

    [Theory]
    // A WebMercatorQuad tile matrix level is not the MapLibre camera zoom: these tiles are 256px,
    // so a client consuming them as a 256px raster source sits one camera zoom below the level.
    [InlineData(4, 256, 3.0)]
    [InlineData(4, 512, 4.0)]
    [InlineData(14, 256, 13.0)]
    [InlineData(14, 512, 14.0)]
    public void FromWebMercatorExtent_TileEnvelope_IsOneZoomBelowMatrixLevelAt256Px(
        int matrixLevel,
        int tilePixels,
        double expectedZoom)
    {
        var tileSpan = WorldSpanMeters / Math.Pow(2, matrixLevel);
        var minX = -WorldSpanMeters / 2.0;
        var extent = new SkiaMapRenderer.RenderExtent(minX, 0, minX + tileSpan, tileSpan);

        var zoom = RenderZoom.FromWebMercatorExtent(extent, tilePixels, tilePixels);

        zoom.Level!.Value.Should().BeApproximately(expectedZoom, 1e-9);
    }

    [UnitTest]
    public void FromWebMercatorExtent_MismatchedAspect_FitsTheMoreConstrainedAxis()
    {
        // MapLibre's cameraForBoxAndBearing takes Math.min(scaleX, scaleY), so the axis that needs
        // to zoom out furthest wins.
        var quarterWorld = WorldSpanMeters / 4.0;
        var extent = new SkiaMapRenderer.RenderExtent(0, 0, quarterWorld, quarterWorld / 2.0);

        var zoom = RenderZoom.FromWebMercatorExtent(extent, 512, 512);

        // x: log2(512 / (512 * 1/4)) = 2 ; y: log2(512 / (512 * 1/8)) = 3 -> min = 2
        zoom.Level!.Value.Should().BeApproximately(2.0, 1e-9);
    }

    [UnitTest]
    public void FromExtent_UnsupportedCrs_IsNotDerivableWithAReason()
    {
        var zoom = RenderZoom.FromExtent(
            new SkiaMapRenderer.RenderExtent(400000, 5000000, 410000, 5010000),
            512,
            512,
            25832);

        zoom.Level.Should().BeNull();
        zoom.NotDerivableReason.Should().Contain("25832");
    }

    [Theory]
    [InlineData(0, 512)]
    [InlineData(512, 0)]
    [InlineData(-1, 512)]
    public void FromWebMercatorExtent_NonPositiveDimensions_IsNotDerivable(int imageWidth, int imageHeight)
    {
        var extent = new SkiaMapRenderer.RenderExtent(0, 0, 1000, 1000);

        var zoom = RenderZoom.FromWebMercatorExtent(extent, imageWidth, imageHeight);

        zoom.Level.Should().BeNull();
        zoom.NotDerivableReason.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void FromWebMercatorExtent_DegenerateExtent_IsNotDerivable()
    {
        var extent = new SkiaMapRenderer.RenderExtent(0, 0, 0, 0);

        var zoom = RenderZoom.FromWebMercatorExtent(extent, 512, 512);

        zoom.Level.Should().BeNull();
        zoom.NotDerivableReason.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void NotDerivable_WithoutReason_Throws()
    {
        var act = () => RenderZoom.NotDerivable("  ");

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void FromExtent_WebMercatorAlias_DerivesSameZoomAsEpsg3857()
    {
        var half = WorldSpanMeters / 2.0;
        var extent = new SkiaMapRenderer.RenderExtent(-half, -half, half, half);

        var canonical = RenderZoom.FromExtent(extent, 512, 512, WebMercator);
        var alias = RenderZoom.FromExtent(extent, 512, 512, 900913);

        alias.Level.Should().NotBeNull();
        alias.Level!.Value.Should().BeApproximately(canonical.Level!.Value, 1e-9);
    }
}

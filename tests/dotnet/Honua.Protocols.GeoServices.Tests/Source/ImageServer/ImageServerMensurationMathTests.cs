// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Unit coverage for the ground-mensuration math backing Basic ImageServer measure (#2734).
/// Verifies that Web Mercator distances are corrected by 1/cos(latitude), that geographic
/// (degree) inputs are measured geodesically rather than as degrees-as-meters, that bearings
/// carry the meridian-convergence correction, and that area handles the antimeridian and uses
/// the signed-area centroid.
/// </summary>
public sealed class ImageServerMensurationMathTests
{
    private const double MeanRadius = 6371008.8;

    [UnitTest]
    public void GeodesicDistanceMeters_OneDegreeLatitude_IsAboutOneEleventhOfADegreeArc()
    {
        // 1° of latitude on the mean-radius sphere is R * pi / 180 ≈ 111194.9 m.
        var distance = ImageServerMensurationMath.GeodesicDistanceMeters(0, 0, 0, 1);
        distance.Should().BeApproximately(MeanRadius * Math.PI / 180d, 1e-3);
    }

    [UnitTest]
    public void GeodesicDistanceMeters_OneDegreeLongitudeAt40N_IsAbout85Km_NotOne()
    {
        // EPSG:4269 (NAD83) 1° of longitude at 40°N. The pre-fix planar path returned the raw
        // degree delta (1.0) as "meters"; the ground distance is ~85 km.
        var distance = ImageServerMensurationMath.GeodesicDistanceMeters(0, 40, 1, 40);
        distance.Should().BeInRange(84_500d, 85_500d);
    }

    [UnitTest]
    public void TryConvertToLonLat_WebMercator1000mAt60N_MeasuresAbout1000GroundMeters()
    {
        // Two points 1000 ground-meters apart (north-south) at 60°N. In Web Mercator the
        // northing delta is ~2000 m (scale = 1/cos(60°) = 2), so the pre-fix planar path
        // reported ~2000 m. After inverse-Mercator normalization the ground distance is ~1000 m.
        const double lat0 = 60d;
        const double lon0 = 10d;
        var groundMeters = 1000d;
        var dLat = groundMeters / (MeanRadius * Math.PI / 180d);

        var (ax, ay) = WebMercatorMath.LonLatToWebMercator(lon0, lat0);
        var (bx, by) = WebMercatorMath.LonLatToWebMercator(lon0, lat0 + dLat);

        // The raw Web Mercator northing delta overstates the ground distance ~2x.
        ImageServerMensurationMath.PlanarDistanceMeters(ax, ay, bx, by)
            .Should().BeApproximately(2000d, 30d);

        ImageServerMensurationMath.TryConvertToLonLat(ax, ay, 3857, out var alon, out var alat).Should().BeTrue();
        ImageServerMensurationMath.TryConvertToLonLat(bx, by, 3857, out var blon, out var blat).Should().BeTrue();

        ImageServerMensurationMath.GeodesicDistanceMeters(alon, alat, blon, blat)
            .Should().BeApproximately(1000d, 1d);
    }

    [UnitTest]
    public void TryConvertToLonLat_WebMercatorAlias102100_ConvertsLikeCanonical3857()
    {
        var (x, y) = WebMercatorMath.LonLatToWebMercator(-73.5, 45.2);
        ImageServerMensurationMath.TryConvertToLonLat(x, y, 102100, out var lon, out var lat).Should().BeTrue();
        lon.Should().BeApproximately(-73.5, 1e-6);
        lat.Should().BeApproximately(45.2, 1e-6);
    }

    [UnitTest]
    public void TryConvertToLonLat_ProjectedUtmSrid_ReturnsFalse()
    {
        // UTM zone 18N (EPSG:32618) has no in-process inverse; the caller must use a transform
        // service or fall back to planar meters.
        ImageServerMensurationMath.TryConvertToLonLat(500000, 5000000, 32618, out _, out _).Should().BeFalse();
    }

    [UnitTest]
    public void IsGeographicSrid_ClassifiesKnownAndRangeCodes()
    {
        ImageServerMensurationMath.IsGeographicSrid(4326).Should().BeTrue();
        ImageServerMensurationMath.IsGeographicSrid(4269).Should().BeTrue();
        ImageServerMensurationMath.IsGeographicSrid(4258).Should().BeTrue();
        ImageServerMensurationMath.IsGeographicSrid(3857).Should().BeFalse();
        ImageServerMensurationMath.IsGeographicSrid(32618).Should().BeFalse();
    }

    [UnitTest]
    public void InitialBearingDegrees_DiagonalAt60N_IsCorrectedBelow45Degrees()
    {
        // From (0,60) to (1,61): the pre-fix planar azimuth atan2(dx,dy) was exactly 45°. The
        // true initial bearing folds in meridian convergence and is well below 45°.
        var bearing = ImageServerMensurationMath.InitialBearingDegrees(0, 60, 1, 61);
        bearing.Should().BeInRange(24d, 28d);
        bearing.Should().BeLessThan(45d);
    }

    [UnitTest]
    public void InitialBearingDegrees_DueEast_IsNinetyDegrees()
    {
        var bearing = ImageServerMensurationMath.InitialBearingDegrees(0, 0, 1, 0);
        bearing.Should().BeApproximately(90d, 1e-6);
    }

    [UnitTest]
    public void GeodesicRingAreaSquareMeters_OneDegreeSquareAtEquator_IsAboutExpected()
    {
        // A 1°×1° cell at the equator ≈ (111194.9 m)² ≈ 1.236e10 m².
        var ring = new (double Lon, double Lat)[]
        {
            (0, 0), (1, 0), (1, 1), (0, 1), (0, 0)
        };
        var area = ImageServerMensurationMath.GeodesicRingAreaSquareMeters(ring);
        var expected = Math.Pow(MeanRadius * Math.PI / 180d, 2);
        area.Should().BeApproximately(expected, expected * 0.01);
    }

    [UnitTest]
    public void GeodesicRingAreaSquareMeters_AntimeridianCrossingRing_IsFiniteAndSmall()
    {
        // A ~2°×1° cell straddling the antimeridian (179°E .. -179°E). Raw longitude deltas would
        // project a ~358°-wide polygon; longitude unwrapping keeps it a small finite area.
        var ring = new (double Lon, double Lat)[]
        {
            (179, 0), (-179, 0), (-179, 1), (179, 1), (179, 0)
        };
        var area = ImageServerMensurationMath.GeodesicRingAreaSquareMeters(ring);

        var reference = new (double Lon, double Lat)[]
        {
            (0, 0), (2, 0), (2, 1), (0, 1), (0, 0)
        };
        var referenceArea = ImageServerMensurationMath.GeodesicRingAreaSquareMeters(reference);

        area.Should().BeApproximately(referenceArea, referenceArea * 0.01);
    }

    [UnitTest]
    public void ShadowHeightMeters_At45Degrees_EqualsShadowLength()
    {
        // tan(45°) = 1, so the object height equals the measured shadow length exactly.
        ImageServerMensurationMath.ShadowHeightMeters(100d, 45d).Should().BeApproximately(100d, 1e-9);
    }

    [UnitTest]
    public void ShadowHeightMeters_At30Degrees_IsShadowLengthTimesTan30()
    {
        // Independently computed: h = L·tan(30°) = 50 · 0.5773502691896257 = 28.867513459481287 m.
        ImageServerMensurationMath.ShadowHeightMeters(50d, 30d)
            .Should().BeApproximately(28.867513459481287d, 1e-9);
    }

    [UnitTest]
    public void ShadowHeightMeters_At60Degrees_IsShadowLengthTimesTan60()
    {
        // Independently computed: h = L·tan(60°) = 200 · 1.7320508075688772 = 346.41016151377545 m.
        ImageServerMensurationMath.ShadowHeightMeters(200d, 60d)
            .Should().BeApproximately(346.41016151377545d, 1e-9);
    }

    [UnitTest]
    public void ShadowHeightMeters_LowSunCastsLongShadow_ForModestHeight()
    {
        // A 10 m shadow under a low 15° sun implies a short object: h = 10·tan(15°) ≈ 2.679491924 m.
        ImageServerMensurationMath.ShadowHeightMeters(10d, 15d)
            .Should().BeApproximately(2.6794919243112270d, 1e-9);
    }

    [UnitTest]
    public void SignedAreaCentroid_LShapedPolygon_DiffersFromVertexMean()
    {
        // An L-shaped polygon: the true area centroid is not the mean of the vertices.
        var ring = new (double X, double Y)[]
        {
            (0, 0), (4, 0), (4, 1), (1, 1), (1, 4), (0, 4), (0, 0)
        };
        var (cx, cy) = ImageServerMensurationMath.SignedAreaCentroid(ring, unwrapLongitudes: false);

        // Area centroid = ([0,4]x[0,1] arm @ (2,0.5), area 4) + ([0,1]x[1,4] arm @ (0.5,2.5),
        // area 3) → (9.5/7, 9.5/7) = (1.35714, 1.35714), distinct from the vertex mean (1.6667).
        cx.Should().BeApproximately(1.3571429, 1e-4);
        cy.Should().BeApproximately(1.3571429, 1e-4);
    }
}

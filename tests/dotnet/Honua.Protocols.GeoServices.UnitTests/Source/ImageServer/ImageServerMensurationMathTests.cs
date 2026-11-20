// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.GeometryService.Services;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

public sealed class ImageServerMensurationMathTests
{
    [UnitTheory]
    [InlineData(0d, 0d, 0d)]
    [InlineData(1d, 0d, 111319.490793d)]
    [InlineData(0d, 1d, 110574.388558d)]
    public void GeodesicDistanceMeters_CoincidentAndEquatorialLimitsRemainFinite(
        double longitude, double latitude, double expectedMeters)
    {
        ImageServerMensurationMath.GeodesicDistanceMeters(0d, 0d, longitude, latitude)
            .Should().BeApproximately(expectedMeters, 0.001d);
    }

    private const double MeanRadius = 6371008.8;

    [UnitTest]
    public void GeodesicDistanceMeters_OneDegreeLatitude_IsAboutOneEleventhOfADegreeArc()
    {
        // 1° of latitude at the equator on the WGS 84 ellipsoid is the meridional radius
        // times one degree, about 110574.3 m. A mean-radius haversine returns 111195 m.
        var distance = ImageServerMensurationMath.GeodesicDistanceMeters(0, 0, 0, 1);
        distance.Should().BeApproximately(110574.3d, 0.1d);
    }

    [UnitTest]
    public void GeodesicDistanceMeters_OneDegreeLongitudeAt40N_IsAbout85Km_NotOne()
    {
        var distance = ImageServerMensurationMath.GeodesicDistanceMeters(0, 40, 1, 40);
        distance.Should().BeInRange(84_500d, 85_500d);
    }

    [UnitTest]
    public void TryConvertToLonLat_WebMercator1000mAt60N_MeasuresAbout1000GroundMeters()
    {
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
            .Should().BeApproximately(1000d, 15d);
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
        // A 1°×1° cell at the equator on the WGS 84 ellipsoid is about
        // 110574 m × 111319 m ≈ 1.2309e10 m². The old equirectangular sphere was 1.236e10.
        var ring = new (double Lon, double Lat)[]
        {
            (0, 0), (1, 0), (1, 1), (0, 1), (0, 0)
        };
        var area = ImageServerMensurationMath.GeodesicRingAreaSquareMeters(ring);
        const double expected = 1.2309072079294865e10;
        area.Should().BeApproximately(expected, expected * 0.005);
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

    [UnitTest]
    public void GeodesicPolygonAreaSquareMeters_PolygonWithInteriorHole_SubtractsHoleArea()
    {
        var outer = new (double Lon, double Lat)[]
        {
            (0d, 0d), (1d, 0d), (1d, 1d), (0d, 1d), (0d, 0d)
        };
        var hole = new (double Lon, double Lat)[]
        {
            (0.25d, 0.25d), (0.25d, 0.75d), (0.75d, 0.75d), (0.75d, 0.25d), (0.25d, 0.25d)
        };

        var outerArea = ImageServerMensurationMath.GeodesicRingAreaSquareMeters(outer);
        var holeArea = ImageServerMensurationMath.GeodesicRingAreaSquareMeters(hole);
        var polygonArea = ImageServerMensurationMath.GeodesicPolygonAreaSquareMeters([outer, hole]);

        polygonArea.Should().BeApproximately(outerArea - holeArea, outerArea * 1e-6);
    }

    [UnitTest]
    public void GeodesicRingAreaSquareMeters_UsesLambertAzimuthalEqualAreaForLargeCap()
    {
        // A regular geodesic circle of radius d = 1,000 km around (0, 0) on the ellipsoid.
        // In AEQD (rho = d), planar area would be pi * d^2.
        // In LAEA (rho = 2*R*sin(d/(2*R))), planar area is 4*pi*R^2*sin^2(d/(2*R)) < pi * d^2.
        const double radiusMeters = 1_000_000d;
        const int vertices = 180;
        var ring = new (double Lon, double Lat)[vertices + 1];
        for (var i = 0; i < vertices; i++)
        {
            var bearing = i * (360d / vertices);
            ring[i] = Wgs84Vincenty.Direct(0d, 0d, bearing, radiusMeters);
        }

        ring[vertices] = ring[0];

        var measuredArea = ImageServerMensurationMath.GeodesicRingAreaSquareMeters(ring);
        var R = ImageServerMensurationMath.AuthalicEarthRadiusMeters;
        var rhoLaea = 2d * R * Math.Sin(radiusMeters / (2d * R));
        var expectedLaeaCapArea = Math.PI * rhoLaea * rhoLaea;
        var inflatedAeqdArea = Math.PI * radiusMeters * radiusMeters;

        // With 180 vertices, inscribed polygon factor is cos(pi/180) ~ 0.99985, while the
        // difference between AEQD and LAEA at 1,000 km is d^2 / (12 * R^2) ~ 0.205%.
        Math.Abs(measuredArea - expectedLaeaCapArea).Should().BeLessThan(
            Math.Abs(measuredArea - inflatedAeqdArea));
    }

    [UnitTest]
    public void ComputeGroundPixelAreaSquareMeters_ConvertsGeographicWebMercatorAndFootPixels()
    {
        // 1. Geographic (4326) at 60°N: 0.001° x 0.001° pixel
        var nativeDegArea = 1e-6;
        var degGroundArea = ImageServerMensurationMath.ComputeGroundPixelAreaSquareMeters(
            nativeDegArea, centerY: 60d, srid: 4326, isGeographic: true, linearMetersPerUnit: 1d);
        var metersPerDeg = ImageServerMensurationMath.MeanEarthRadiusMeters * Math.PI / 180d;
        var expectedDegArea = nativeDegArea * metersPerDeg * metersPerDeg * Math.Cos(60d * Math.PI / 180d);
        degGroundArea.Should().BeApproximately(expectedDegArea, 1e-6);

        // 2. Web Mercator (3857) at 60°N: 10m x 10m Mercator pixel -> 100 * cos^2(60°) = 25 m^2
        var (_, mercY60) = WebMercatorMath.LonLatToWebMercator(0d, 60d);
        var mercGroundArea = ImageServerMensurationMath.ComputeGroundPixelAreaSquareMeters(
            100d, centerY: mercY60, srid: 3857, isGeographic: false, linearMetersPerUnit: 1d);
        mercGroundArea.Should().BeApproximately(25d, 1e-4);

        // 3. US Survey Foot (2263): 10 ftUS x 10 ftUS pixel
        var surveyFootFactor = 1200d / 3937d;
        var footGroundArea = ImageServerMensurationMath.ComputeGroundPixelAreaSquareMeters(
            100d, centerY: 0d, srid: 2263, isGeographic: false, linearMetersPerUnit: surveyFootFactor);
        footGroundArea.Should().BeApproximately(100d * surveyFootFactor * surveyFootFactor, 1e-9);
    }

    [UnitTest]
    public async Task ImageServerMeasureHandler_PolygonWithHole_SubtractsHoleAreaAndClosesEachRingPerimeter()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>())
            .Returns(ImageServerTestFixtures.CreateSmallRasterInfo(1) with { Srid = 32618 });

        var handler = new ImageServerMeasureHandler(
            rasterStore,
            NullLogger<ImageServerMeasureHandler>.Instance);

        var context = ImageServerTestFixtures.CreateContext("/rest/services/test/ImageServer/measure");
        // 100m x 100m exterior square (area 10,000 m^2, perimeter 400 m)
        // with a 20m x 20m interior hole (area 400 m^2, perimeter 80 m).
        // Expected area = 9,600 m^2; expected perimeter = 480 m (no phantom jump edge between rings).
        var values = new Dictionary<string, StringValues>
        {
            ["measureOperation"] = "esriMensurationAreaAndPerimeter",
            ["geometryType"] = "esriGeometryPolygon",
            ["fromGeometry"] = """
            {
                "rings": [
                    [[0,0],[0,100],[100,100],[100,0],[0,0]],
                    [[40,40],[60,40],[60,60],[40,60],[40,40]]
                ],
                "spatialReference": {"wkid": 32618}
            }
            """,
            ["linearUnit"] = "esriMeters",
            ["areaUnit"] = "esriSquareMeters"
        };

        var result = await handler.MeasureAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);

        var area = doc.RootElement.GetProperty("area").GetProperty("value").GetDouble();
        var perimeter = doc.RootElement.GetProperty("perimeter").GetProperty("value").GetDouble();

        area.Should().BeApproximately(9_600d, 1e-6);
        perimeter.Should().BeApproximately(480d, 1e-6);
    }

    [UnitTest]
    public async Task ImageServerCalculateVolumeHandler_GeographicDem_ScalesDegreePixelsToGroundSquareMeters()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        var primaryRaster = ImageServerTestFixtures.CreateSmallRasterInfo(1) with
        {
            Id = 10,
            Srid = 4326,
            SensorMetadata = new RasterSensorMetadata { RasterDataId = 10, DemSource = "2" }
        };
        var demRaster = new RasterInfo
        {
            Id = 20,
            LayerId = 2,
            Name = "dem-4326",
            Width = 100,
            Height = 100,
            BandCount = 1,
            PixelType = "32BF",
            Srid = 4326,
            GeoTransform = [0.0, 0.001, 0.0, 60.05, 0.0, -0.001],
            Extent = new RasterExtent { XMin = 0.0, YMin = 59.95, XMax = 0.1, YMax = 60.05, Srid = 4326 }
        };

        rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>()).Returns(primaryRaster);
        rasterStore.GetSensorMetadataAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, RasterSensorMetadata> { [10] = primaryRaster.SensorMetadata! });
        rasterStore.GetPrimaryRasterInfoAsync(2, Arg.Any<CancellationToken>()).Returns(demRaster);
        rasterStore.QueryRastersAsync(2, Arg.Any<RasterSelectionQuery>(), Arg.Any<CancellationToken>())
            .Returns([demRaster]);
        rasterStore.ReadClippedBandVectorsAsync(
                2,
                Arg.Any<long[]>(),
                Arg.Any<RasterMergeStrategy>(),
                Arg.Any<byte[]>(),
                Arg.Any<int?>(),
                Arg.Any<int[]?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new RasterBandVectorSet
            {
                Bands = [1],
                Pixels = [[10.0]],
                BoundingPixelCount = 1,
                ExceededPixelBudget = false
            });

        var handler = new ImageServerCalculateVolumeHandler(
            rasterStore,
            Options.Create(new ImageServerCalculateVolumeOptions()),
            NullLogger<ImageServerCalculateVolumeHandler>.Instance);

        var context = ImageServerTestFixtures.CreateContext("/rest/services/test/ImageServer/calculateVolume");
        var values = new Dictionary<string, StringValues>
        {
            ["geometries"] = """[{"xmin":0,"ymin":59.99,"xmax":0.01,"ymax":60.01}]""",
            ["geometryType"] = "esriGeometryEnvelope",
            ["baseType"] = "0",
            ["constantZ"] = "0"
        };

        var result = await handler.CalculateVolumeAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);

        var firstResult = doc.RootElement.GetProperty("results")[0];
        var reportedArea = firstResult.GetProperty("area").GetDouble();
        var reportedCut = firstResult.GetProperty("cut").GetDouble();

        var metersPerDeg = ImageServerMensurationMath.MeanEarthRadiusMeters * Math.PI / 180d;
        var expectedPixelArea = 1e-6 * metersPerDeg * metersPerDeg * Math.Cos(60d * Math.PI / 180d);

        reportedArea.Should().BeApproximately(expectedPixelArea, 1e-3);
        reportedCut.Should().BeApproximately(10.0 * expectedPixelArea, 1e-2);
    }

    [UnitTest]
    public async Task ImageServerProjectHandler_ProjectedInSrWithExplicitDatumTransformation_ResolvesGeodeticBaseSrid()
    {
        var crsDetection = Substitute.For<ICrsDetectionService>();
        var crsRegistry = Substitute.For<ICrsRegistry>();
        crsRegistry.IsSridSupportedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        crsRegistry.ResolveBySridAsync(26910, Arg.Any<CancellationToken>())
            .Returns(new CrsDefinition("EPSG:26910", 26910, AxisOrder.EastNorth, false)
            {
                Wkt = """PROJCS["NAD83 / UTM zone 10N",GEOGCS["NAD83",AUTHORITY["EPSG","4269"]],AUTHORITY["EPSG","26910"]]"""
            });
        crsRegistry.ResolveBySridAsync(4326, Arg.Any<CancellationToken>())
            .Returns(new CrsDefinition("EPSG:4326", 4326, AxisOrder.EastNorth, true));

        var datumSelection = new DatumTransformationSelection(
            Wkid: 108001,
            Name: "NAD_1983_To_WGS_1984_1",
            FromSrid: 4269,
            ToSrid: 4326,
            TransformForward: true,
            ProjPipeline: "+proj=noop");
        var datumCatalog = Substitute.For<IDatumTransformationCatalog>();
        datumCatalog.TryGetByWkid(108001, 4269, 4326, out Arg.Any<DatumTransformationSelection>())
            .Returns(callInfo =>
            {
                callInfo[3] = datumSelection;
                return true;
            });

        var operationService = Substitute.For<IGeometryOperationService>();
        operationService.ProjectAsync(
                Arg.Any<byte[]>(),
                26910,
                4326,
                datumSelection,
                Arg.Any<CancellationToken>())
            .Returns(new NetTopologySuite.IO.WKBWriter().Write(new NetTopologySuite.Geometries.Point(-123.0, 0.0)));

        var projection = new ImageServerCoordinateProjection(
            new SpatialReferenceResolver(crsDetection, crsRegistry),
            datumCatalog);
        var handler = new ImageServerProjectHandler(
            operationService,
            projection,
            NullLogger<ImageServerProjectHandler>.Instance);

        var context = ImageServerTestFixtures.CreateContext("/rest/services/test/ImageServer/project");
        var values = new Dictionary<string, StringValues>
        {
            ["inSR"] = "26910",
            ["outSR"] = "4326",
            ["datumTransformation"] = "108001",
            ["geometries"] = """{"geometryType":"esriGeometryPoint","geometries":[{"x":500000,"y":0}]}"""
        };

        var result = await handler.ProjectAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        await operationService.Received(1).ProjectAsync(
            Arg.Any<byte[]>(),
            26910,
            4326,
            datumSelection,
            Arg.Any<CancellationToken>());
    }
}

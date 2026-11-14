// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Crs;

public sealed class GeoServicesDatumTransformationCatalogTests
{
    private const int Nad83 = 4269;
    private const int Wgs84 = 4326;
    private const int Nad27 = 4267;
    private const int Nad83Harn = 4152;
    private const int Nad83Nsrs2007 = 4759;
    private const int Nad83_2011 = 6318;

    private readonly IDatumTransformationCatalog _catalog = GeoServicesDatumTransformationCatalog.Create();

    [UnitTest]
    public void TryGetDefault_Nad83ToWgs84_ReturnsEsriDefaultWkidAndPipeline()
    {
        var found = _catalog.TryGetDefault(Nad83, Wgs84, out var selection);

        found.Should().BeTrue();
        selection.Should().NotBeNull();
        selection!.Wkid.Should().Be(108001);
        selection.Name.Should().Be("NAD_1983_To_WGS_1984_1");
        selection.FromSrid.Should().Be(Nad83);
        selection.ToSrid.Should().Be(Wgs84);
        selection.EpsgOperationCode.Should().Be(1188);
        // EPSG 1188 is a null transformation; the pipeline is the exact-identity +proj=noop.
        selection.ProjPipeline.Should().Be("+proj=noop");
        selection.TransformForward.Should().BeTrue();
    }

    [UnitTest]
    public void TryGetDefault_InverseDirection_IsSynthesizedAsInverse()
    {
        var found = _catalog.TryGetDefault(Wgs84, Nad83, out var selection);

        found.Should().BeTrue();
        selection.Should().NotBeNull();
        selection!.FromSrid.Should().Be(Wgs84);
        selection.ToSrid.Should().Be(Nad83);
        selection.TransformForward.Should().BeFalse();
        selection.ProjPipeline.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void TryGetDefault_Nad27ToNad83_ReturnsNadconWithRequiredGrid()
    {
        var found = _catalog.TryGetDefault(Nad27, Nad83, out var selection);

        found.Should().BeTrue();
        selection!.Wkid.Should().Be(1241);
        selection.Name.Should().Be("NAD_1927_To_NAD_1983_NADCON");
        selection.RequiredGrids.Should().Contain("us_noaa_conus.tif");
        selection.ProjPipeline.Should().Contain("hgridshift");
        selection.ProjPipeline.Should().Contain("us_noaa_conus.tif");
    }

    [UnitTest]
    public void TryGetForEnvelope_Anchorage_SelectsAlaskaGrid()
    {
        var found = _catalog.TryGetForEnvelope(Nad27, Nad83, -150.2, 61.1, -149.7, 61.3, out var selection);

        found.Should().BeTrue();
        selection!.ProjPipeline.Should().Contain("us_noaa_alaska.tif");
    }

    [UnitTest]
    public void TryGetForEnvelope_AleutiansAcrossAntimeridian_SelectsAlaskaGrid()
    {
        _catalog.TryGetForEnvelope(Nad27, Nad83, 179, 51, -179, 53, out var selection)
            .Should().BeTrue();
        selection!.RequiredGrids.Should().Contain("us_noaa_alaska.tif");
    }

    [UnitTest]
    public void TryGetForEnvelope_Denver_SelectsConusGrid()
    {
        var found = _catalog.TryGetForEnvelope(Nad27, Nad83, -105.1, 39.6, -104.8, 39.8, out var selection);

        found.Should().BeTrue();
        selection!.Wkid.Should().Be(1241);
        selection.ProjPipeline.Should().Contain("us_noaa_conus.tif");
    }

    [UnitTest]
    public void TryGetForEnvelope_BoxInsideConusAndCanada_SelectsCanadianPriority()
    {
        var found = _catalog.TryGetForEnvelope(Nad27, Nad83, -79.6, 43.6, -79.2, 43.9, out var selection);

        found.Should().BeTrue();
        selection!.RequiredGrids.Should().Contain("ca_nrc_ntv2_0.tif");
    }

    [UnitTheory]
    [InlineData(-170, 60, 170, 65)]
    [InlineData(-180, 60, 180, 65)]
    [InlineData(double.NaN, 60, -150, 65)]
    [InlineData(-150, 65, -149, 60)]
    public void TryGetForEnvelope_InvalidOrOutsideAlaskaArc_ReturnsFalse(
        double west, double south, double east, double north)
    {
        _catalog.TryGetForEnvelope(Nad27, Nad83, west, south, east, north, out var selection)
            .Should().BeFalse();
        selection.Should().BeNull();
    }

    [UnitTheory]
    [InlineData(-79.4, 43.7)]
    [InlineData(-110, 50)]
    [InlineData(-110, 65)]
    [InlineData(-110, 80)]
    public void TryGetForEnvelope_InsideCanadianParentGrid_SelectsCanada(double longitude, double latitude)
    {
        _catalog.TryGetForEnvelope(Nad27, Nad83, longitude, latitude, longitude, latitude, out var selection)
            .Should().BeTrue();
        selection!.RequiredGrids.Should().Equal("ca_nrc_ntv2_0.tif");
    }

    [UnitTheory]
    [InlineData(Nad27, Nad83)]
    [InlineData(Nad83, Nad27)]
    public void TryGetForEnvelope_InsideCanadianBoundsOutsideGrid_SelectsConus(int fromSrid, int toSrid)
    {
        _catalog.TryGetForEnvelope(fromSrid, toSrid, -100.1, 43.9, -99.9, 44.1, out var selection)
            .Should().BeTrue();
        selection!.RequiredGrids.Should().Equal("us_noaa_conus.tif");
    }

    [UnitTest]
    public void TryGetForEnvelope_CrossesCanadianCoverageGap_SelectsConus()
    {
        _catalog.TryGetForEnvelope(Nad27, Nad83, -89, 46, -87, 48, out var selection)
            .Should().BeTrue();
        selection!.RequiredGrids.Should().Equal("us_noaa_conus.tif");
    }

    [UnitTest]
    public void TryGetByWkid_ConusOperation_IgnoresRegionalPriority()
    {
        _catalog.TryGetByWkid(1241, Nad27, Nad83, out var selection).Should().BeTrue();
        selection!.RequiredGrids.Should().Equal("us_noaa_conus.tif");
    }

    [UnitTest]
    public void TryGetForEnvelope_CanadaInverse_UsesSamePriority()
    {
        _catalog.TryGetForEnvelope(Nad83, Nad27, -79.6, 43.6, -79.2, 43.9, out var selection)
            .Should().BeTrue();
        selection!.RequiredGrids.Should().Contain("ca_nrc_ntv2_0.tif");
        selection.TransformForward.Should().BeFalse();
    }

    [UnitTest]
    public void TryGetForEnvelope_HawaiiNad27_DoesNotUseConus()
    {
        var found = _catalog.TryGetForEnvelope(Nad27, Nad83, -158.1, 21.2, -157.7, 21.4, out var selection);

        found.Should().BeFalse();
        selection.Should().BeNull();
    }

    [UnitTest]
    public void TryGetForEnvelope_Honolulu_SelectsOldHawaiianGrid()
    {
        var found = _catalog.TryGetForEnvelope(4135, 4269, -158.1, 21.2, -157.7, 21.4, out var selection);

        found.Should().BeTrue();
        selection!.ProjPipeline.Should().Contain("us_noaa_hawaii.tif");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(Nad83Harn, 1580, "NAD_1983_HARN_To_WGS_1984_1")]
    [InlineData(Nad83Nsrs2007, 15931, "NAD_1983_NSRS2007_To_WGS_1984_1")]
    [InlineData(Nad83_2011, 9774, "NAD_1983_2011_To_WGS_1984_1")]
    public void TryGetDefault_Wgs84RealizationPair_ReturnsNullTransformPipeline(int fromSrid, int epsgOp, string name)
    {
        // NAD83(HARN)/NSRS2007/2011 -> WGS84 are EPSG null transformations (#1501).
        // They are seeded as the exact-identity +proj=noop, which PostGIS applies through
        // the 3-argument ST_Transform chokepoint (validated against the runtime).
        var found = _catalog.TryGetDefault(fromSrid, Wgs84, out var selection);

        found.Should().BeTrue();
        selection.Should().NotBeNull();
        selection!.Name.Should().Be(name);
        selection.FromSrid.Should().Be(fromSrid);
        selection.ToSrid.Should().Be(Wgs84);
        selection.EpsgOperationCode.Should().Be(epsgOp);
        selection.ProjPipeline.Should().Be("+proj=noop");
        selection.RequiredGrids.Should().BeEmpty();
        selection.TransformForward.Should().BeTrue();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(Nad83Harn)]
    [InlineData(Nad83Nsrs2007)]
    [InlineData(Nad83_2011)]
    public void TryGetDefault_Wgs84RealizationInverse_IsSynthesizedAsInverse(int fromSrid)
    {
        // The inverse (WGS84 -> realization) is synthesized; for a null transform the
        // inverse pipeline is the same exact-identity +proj=noop.
        var found = _catalog.TryGetDefault(Wgs84, fromSrid, out var selection);

        found.Should().BeTrue();
        selection!.FromSrid.Should().Be(Wgs84);
        selection.ToSrid.Should().Be(fromSrid);
        selection.TransformForward.Should().BeFalse();
        selection.ProjPipeline.Should().Be("+proj=noop");
    }

    [UnitTest]
    public void TryGetDefault_UnknownPair_ReturnsFalse()
    {
        var found = _catalog.TryGetDefault(2154 /* RGF93 / Lambert-93 */, Wgs84, out var selection);

        found.Should().BeFalse();
        selection.Should().BeNull();
    }

    [UnitTest]
    public void TryGetByWkid_KnownWkidForForwardPair_Resolves()
    {
        var found = _catalog.TryGetByWkid(108001, Nad83, Wgs84, out var selection);

        found.Should().BeTrue();
        selection!.Wkid.Should().Be(108001);
        selection.FromSrid.Should().Be(Nad83);
        selection.ToSrid.Should().Be(Wgs84);
        selection.TransformForward.Should().BeTrue();
    }

    [UnitTest]
    public void TryGetByWkid_KnownWkidForInversePair_ResolvesInverted()
    {
        var found = _catalog.TryGetByWkid(108001, Wgs84, Nad83, out var selection);

        found.Should().BeTrue();
        selection!.FromSrid.Should().Be(Wgs84);
        selection.ToSrid.Should().Be(Nad83);
        selection.TransformForward.Should().BeFalse();
    }

    [UnitTest]
    public void TryGetByWkid_UnknownWkid_ReturnsFalse()
    {
        var found = _catalog.TryGetByWkid(999999, Nad83, Wgs84, out var selection);

        found.Should().BeFalse();
        selection.Should().BeNull();
    }

    [UnitTest]
    public void TryGetByWkid_KnownWkidButWrongPair_ReturnsFalse()
    {
        // 108001 connects NAD83 <-> WGS84, not NAD83 <-> NAD83(2011).
        var found = _catalog.TryGetByWkid(108001, Nad83, Nad83_2011, out var selection);

        found.Should().BeFalse();
        selection.Should().BeNull();
    }
}

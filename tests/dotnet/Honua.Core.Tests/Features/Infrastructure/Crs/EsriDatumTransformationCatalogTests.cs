// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Crs;

/// <summary>
/// Unit tests for <see cref="EsriDatumTransformationCatalog"/>. Asserts that the
/// curated Esri-default geotransformation table resolves the documented WKID and PROJ
/// pipeline per (fromSrid, toSrid) pair, and that inverse lookups and unknown requests
/// behave as specified (issue #1274).
/// </summary>
public sealed class EsriDatumTransformationCatalogTests
{
    private const int Nad83 = 4269;
    private const int Wgs84 = 4326;
    private const int Nad27 = 4267;
    private const int Nad83_2011 = 6318;

    private readonly IDatumTransformationCatalog _catalog = EsriDatumTransformationCatalog.Create();

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

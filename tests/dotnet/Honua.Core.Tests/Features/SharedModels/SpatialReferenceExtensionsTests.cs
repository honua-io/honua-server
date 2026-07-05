// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Xunit;

namespace Honua.Core.Tests.Features.SharedModels;

/// <summary>
/// Unit coverage for <see cref="SpatialReferenceExtensions"/> SRID normalization.
/// The Web Mercator alias table (#2360) must stay in sync across the query, import,
/// and edit paths so an incoming 102100 spatial reference is treated as EPSG:3857.
/// </summary>
public sealed class SpatialReferenceExtensionsTests
{
    [Theory]
    [InlineData(102100)]
    [InlineData(102113)]
    [InlineData(900913)]
    [InlineData(3785)]
    [InlineData(3857)]
    public void NormalizeWebMercatorSrid_WebMercatorAliases_MapTo3857(int srid)
    {
        SpatialReferenceExtensions.NormalizeWebMercatorSrid(srid).Should().Be(3857);
    }

    [Theory]
    [InlineData(4326)]
    [InlineData(4269)]
    [InlineData(2154)]
    [InlineData(32633)]
    public void NormalizeWebMercatorSrid_OtherSrids_PassThroughUnchanged(int srid)
    {
        SpatialReferenceExtensions.NormalizeWebMercatorSrid(srid).Should().Be(srid);
    }

    // PA-024: GetAuthorityName and GetAuthorityCode must return the OGC-defined values for WKID 4326
    // (CRS84) so that WFS 2.0 / OGC API conformance URNs are formed correctly.

    [Fact]
    public void GetAuthorityName_Wkid4326_ReturnsOgc()
    {
        var sr = new SpatialReference { Wkid = 4326 };
        sr.GetAuthorityName().Should().Be("OGC");
    }

    [Theory]
    [InlineData(3857)]
    [InlineData(4269)]
    [InlineData(32633)]
    public void GetAuthorityName_NonOgcWkid_ReturnsEpsg(int wkid)
    {
        var sr = new SpatialReference { Wkid = wkid };
        sr.GetAuthorityName().Should().Be("EPSG");
    }

    [Fact]
    public void GetAuthorityCode_Wkid4326_ReturnsCrs84Fragment()
    {
        var sr = new SpatialReference { Wkid = 4326 };
        sr.GetAuthorityCode().Should().Be("1.3:CRS84");
    }

    [Theory]
    [InlineData(3857, "3857")]
    [InlineData(4269, "4269")]
    public void GetAuthorityCode_OtherWkids_ReturnsNumericString(int wkid, string expected)
    {
        var sr = new SpatialReference { Wkid = wkid };
        sr.GetAuthorityCode().Should().Be(expected);
    }

    // PA-022: WGS84 layers must advertise both CRS84 (EastNorth) and EPSG:4326 (NorthEast).
    [Fact]
    public void GetSupportedCrsUris_Wgs84Layer_IncludesBothCrs84AndEpsg4326()
    {
        var sr = new SpatialReference { Wkid = 4326 };
        var uris = sr.GetSupportedCrsUris();

        uris.Should().Contain("http://www.opengis.net/def/crs/OGC/1.3/CRS84");
        uris.Should().Contain("http://www.opengis.net/def/crs/EPSG/0/4326");
    }

    // PA-023: Non-WGS84, non-WebMercator layers must NOT advertise EPSG:3857.
    [Fact]
    public void GetSupportedCrsUris_NonWgs84NonWebMercator_DoesNotInclude3857()
    {
        var sr = new SpatialReference { Wkid = 4269 };
        var uris = sr.GetSupportedCrsUris();

        uris.Should().NotContain("http://www.opengis.net/def/crs/EPSG/0/3857");
    }

    // PA-028: https:// CRS84 URI must be normalised to the canonical http:// form.
    [Fact]
    public void FromOgcCrsUri_HttpsCrs84Uri_ReturnsWgs84()
    {
        var result = SpatialReferenceExtensions.FromOgcCrsUri("https://www.opengis.net/def/crs/OGC/1.3/CRS84");

        result.Should().NotBeNull();
        result!.Value.Wkid.Should().Be(4326);
    }

    // PA-028: https:// EPSG URI must resolve to the correct SRID.
    [Fact]
    public void FromOgcCrsUri_HttpsEpsgUri_ReturnsCorrectSrid()
    {
        var result = SpatialReferenceExtensions.FromOgcCrsUri("https://www.opengis.net/def/crs/EPSG/0/3857");

        result.Should().NotBeNull();
        result!.Value.Wkid.Should().Be(3857);
    }
}

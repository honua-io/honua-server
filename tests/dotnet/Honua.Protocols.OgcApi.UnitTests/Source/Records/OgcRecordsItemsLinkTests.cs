// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.Ogc.Api.Common.SpatialReference;
using Honua.Protocols.Ogc.Api.Coverages.Handlers;
using Honua.Protocols.Ogc.Api.Records;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Records;

[Protocol(TestProtocols.OgcApiRecords)]
public sealed class OgcRecordsItemsLinkTests
{
    [UnitTest]
    public void BuildItemsLinks_OffsetNearInt32Max_OmitsNextLink()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?offset=2147483000&limit=1000");

        var links = OgcRecordsEndpoints.BuildItemsLinks(
            context,
            "https://example.test",
            "cat",
            numberMatched: 10,
            limit: 1000,
            offset: 2147483000);

        links.Should().NotContain(link => link.Rel == "next");
        links.Select(link => link.Href).Should().NotContain(href => href.Contains("offset=-", StringComparison.Ordinal));
    }

    [UnitTest]
    public void BuildItemsLinks_WithinRange_EmitsTheFollowingOffset()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?offset=0&limit=10");

        var links = OgcRecordsEndpoints.BuildItemsLinks(
            context,
            "https://example.test",
            "cat",
            numberMatched: 25,
            limit: 10,
            offset: 0);

        links.Should().Contain(link => link.Rel == "next" && link.Href.Contains("offset=10", StringComparison.Ordinal));
    }

    [UnitTest]
    public void TryParseDatetime_RejectsDoublyOpenAndNonRfc3339Values()
    {
        OgcRecordsEndpoints.TryParseDatetime("../..", out _, out _).Should().BeFalse();
        OgcRecordsEndpoints.TryParseDatetime("..", out _, out _).Should().BeFalse();
        OgcRecordsEndpoints.TryParseDatetime("01/15/2020", out _, out _).Should().BeFalse();
        OgcRecordsEndpoints.TryParseDatetime("2020-01-01T12:00:00", out _, out _).Should().BeFalse();
        OgcRecordsEndpoints.TryParseDatetime("/2020-01-01T00:00:00Z", out _, out _).Should().BeFalse();

        OgcRecordsEndpoints.TryParseDatetime("2020-01-01", out var dateFilter, out _).Should().BeTrue();
        dateFilter.Should().NotBeNull();
        OgcRecordsEndpoints.TryParseDatetime("../2020-01-01T00:00:00Z", out var openStart, out _).Should().BeTrue();
        openStart!.Start.Should().BeNull();
        openStart.End.Should().NotBeNull();
    }

    [UnitTest]
    public void TryParseBbox_ValidatesSixElementZAndCrs84GeographicBounds()
    {
        OgcRecordsEndpoints.TryParseBbox("-10,-10,bad,10,10,20", out _, out _).Should().BeFalse();
        OgcRecordsEndpoints.TryParseBbox("-10,-10,30,10,10,20", out _, out _).Should().BeFalse();
        OgcRecordsEndpoints.TryParseBbox("-190,-10,10,10", out _, out _).Should().BeFalse();
        OgcRecordsEndpoints.TryParseBbox("-10,-95,10,10", out _, out _).Should().BeFalse();
        OgcRecordsEndpoints.TryParseBbox("-10,20,10,10", out _, out _).Should().BeFalse();

        OgcRecordsEndpoints.TryParseBbox("-10,-10,5,10,10,20", out var bbox6, out _).Should().BeTrue();
        bbox6.Should().NotBeNull();
        OgcRecordsEndpoints.TryParseBbox("170,-10,-170,10", out var antimeridian, out _).Should().BeTrue();
        antimeridian.Should().NotBeNull();
    }

    [UnitTest]
    public void TryGetGeoJsonOutputFormat_UnsupportedAcceptWithoutQueryFormat_ReturnsNotAcceptable()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Accept = "application/xml";

        var ok = OgcRecordsEndpoints.TryGetGeoJsonOutputFormat(context, f: null, out var errorResult);

        ok.Should().BeFalse();
        errorResult.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status406NotAcceptable);
    }

    [UnitTest]
    public void OgcCoverageSpatialSubset_SupportsPointSliceEqualBoundsAndAntimeridianWrap()
    {
        var raster = new RasterInfo
        {
            Id = 1,
            LayerId = 1,
            Name = "rast",
            Width = 360,
            Height = 180,
            BandCount = 1,
            PixelType = "8BUI",
            Srid = 4326,
            Extent = new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 }
        };

        OgcCoverageSpatialSubset.TryParse(new StringValues("Lon(10),Lat(20)"), raster, 4326, out var pointBounds, out _)
            .Should().BeTrue();
        pointBounds.MinX.Should().Be(10);
        pointBounds.MaxX.Should().Be(10);
        pointBounds.MinY.Should().Be(20);
        pointBounds.MaxY.Should().Be(20);

        OgcCoverageSpatialSubset.TryParse(new StringValues("Lon(170:-170),Lat(-10:10)"), raster, 4326, out var wrappedBounds, out _)
            .Should().BeTrue();
        wrappedBounds.MinX.Should().Be(170);
        wrappedBounds.MaxX.Should().Be(-170);

        OgcCoverageSpatialSubset.TryParse(new StringValues("Lon(0:10),Lat(10:-10)"), raster, 4326, out _, out _)
            .Should().BeFalse();
    }

    [UnitTest]
    public void OgcCoveragesHandler_FormatContentCrsHeader_UsesCanonicalHttpCrsUris()
    {
        OgcCoveragesHandler.FormatContentCrsHeader(outputCrs: null, resultSrid: 4326)
            .Should().Be("<http://www.opengis.net/def/crs/OGC/1.3/CRS84>");

        SpatialReferenceHelpers.TryGetCrsDefinition("EPSG:4326", out var epsg4326).Should().BeTrue();
        OgcCoveragesHandler.FormatContentCrsHeader(epsg4326, resultSrid: 4326)
            .Should().Be("<http://www.opengis.net/def/crs/EPSG/0/4326>");

        OgcCoveragesHandler.FormatContentCrsHeader(outputCrs: null, resultSrid: 3857)
            .Should().Be("<http://www.opengis.net/def/crs/EPSG/0/3857>");
    }
}

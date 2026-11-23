// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Api.Maps.Handlers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

[Protocol(TestProtocols.OgcApiMaps)]
public class OgcMapsConformanceHandlerTests
{
    private readonly OgcMapsConformanceHandler _handler = new(
        NullLogger<OgcMapsConformanceHandler>.Instance);

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_ReturnsConformanceObject()
    {
        var result = await _handler.GetConformanceAsync();

        result.Should().NotBeNull();
        result.ConformsTo.Should().NotBeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_IncludesCoreConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core");
        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/landing-page");
        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/json");
        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/oas30");
        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/core");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_IncludesCollectionMapConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/collection-map");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_IncludesStyledMapConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/styled-map");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_IncludesDatasetMapConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/dataset-map");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_DoesNotOverclaimBackgroundConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().NotContain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/background");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_IncludesCollectionsSelectionConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/collections-selection");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_IncludesDatetimeConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/datetime");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_IncludesFormatConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/png");
        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/jpeg");
        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/tiff");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_IncludesSpatialConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/crs");
        result.ConformsTo.Should().NotContain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/bbox");
        result.ConformsTo.Should().NotContain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/spatial-subsetting");
        result.ConformsTo.Should().Contain(
            "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/scaling");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void OgcCoverageSpatialSubset_SupportsPointSlicesAndAntimeridianWrap()
    {
        var raster = new Honua.Core.Features.Raster.Domain.RasterInfo
        {
            Id = 1,
            LayerId = 1,
            Name = "test",
            Width = 64,
            Height = 64,
            BandCount = 1,
            PixelType = "32BF",
            Srid = 4326,
            Extent = new Honua.Core.Features.Raster.Domain.RasterExtent
            {
                XMin = -180,
                YMin = -90,
                XMax = 180,
                YMax = 90,
                Srid = 4326
            },
            CreatedAt = DateTimeOffset.UtcNow
        };

        Honua.Protocols.Ogc.Api.Coverages.Handlers.OgcCoverageSpatialSubset.TryParse(
            new Microsoft.Extensions.Primitives.StringValues("Lat(45)"),
            raster,
            4326,
            out var pointBounds,
            out var pointError).Should().BeTrue(pointError);
        pointBounds.MinY.Should().Be(45);
        pointBounds.MaxY.Should().Be(47.8125, "a point slice must cover one native grid cell");
        pointBounds.MinX.Should().Be(-180);
        pointBounds.MaxX.Should().Be(180);

        Honua.Protocols.Ogc.Api.Coverages.Handlers.OgcCoverageSpatialSubset.TryParse(
            new Microsoft.Extensions.Primitives.StringValues("Lon(170:-170),Lat(-10:10)"),
            raster,
            4326,
            out var wrapBounds,
            out var wrapError).Should().BeTrue(wrapError);
        wrapBounds.MinX.Should().Be(170);
        wrapBounds.MaxX.Should().Be(-170);
        wrapBounds.MinY.Should().Be(-10);
        wrapBounds.MaxY.Should().Be(10);

        Honua.Protocols.Ogc.Api.Coverages.Handlers.OgcCoverageSpatialSubset.TryParse(
            new Microsoft.Extensions.Primitives.StringValues("Lat(10:-10)"),
            raster,
            4326,
            out _,
            out _).Should().BeFalse();
    }
}

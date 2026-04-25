// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Protocols.Ogc.Api.Maps.Handlers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

/// <summary>
/// Tests for OgcMapsConformanceHandler functionality.
/// </summary>
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
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/core");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_IncludesCollectionMapConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().Contain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/collection-map");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_IncludesDatasetMapConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().Contain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/dataset-map");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_DoesNotOverclaimBackgroundConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().NotContain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/background");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_IncludesCollectionsSelectionConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().Contain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/collections-selection");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_DoesNotOverclaimDatetimeConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().NotContain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/datetime");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_IncludesFormatConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().Contain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/png");
        result.ConformsTo.Should().Contain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/jpeg");
        result.ConformsTo.Should().Contain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/tiff");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_IncludesSpatialConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().Contain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/crs");
        result.ConformsTo.Should().NotContain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/bbox");
        result.ConformsTo.Should().NotContain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/spatial-subsetting");
        result.ConformsTo.Should().Contain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/scaling");
    }
}

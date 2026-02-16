// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.OgcMaps.Handlers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.OgcMaps;

/// <summary>
/// Tests for OgcMapsConformanceHandler functionality.
/// </summary>
[Protocol(Protocols.OgcApiMaps)]
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
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/spatial-subsetting");
        result.ConformsTo.Should().Contain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/scaling");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetConformanceAsync_DoesNotClaimDatasetMapConformance()
    {
        var result = await _handler.GetConformanceAsync();

        result.ConformsTo.Should().NotContain(
            "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/dataset-map");
    }
}

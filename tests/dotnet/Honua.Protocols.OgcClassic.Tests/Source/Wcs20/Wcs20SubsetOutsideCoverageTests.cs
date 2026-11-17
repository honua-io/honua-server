// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

[Collection("Database")]
[Protocol(TestProtocols.Wcs201)]
public sealed class Wcs20SubsetOutsideCoverageTests : IClassFixture<Wcs20SubsetOutsideCoverageTests.RasterApplication>
{
    private readonly List<RasterQuery> _exportQueries;
    private readonly WebAppFixture _fixture;

    public Wcs20SubsetOutsideCoverageTests(RasterApplication application)
    {
        application.RestoreDefaults();
        _exportQueries = application.ExportQueries;
        _fixture = application.Application;
    }

    public sealed class RasterApplication : SubstitutedRasterStoreApplication
    {
        protected override void ConfigureDefaults(IRasterStore rasterStore, List<RasterQuery> exportQueries)
            => Wcs20EndpointsTests.ConfigureRasterStore(rasterStore, exportQueries);
    }

    [IntegrationTheory]
    [InlineData("&SUBSET=Long(-100.0,-99.9)&SUBSET=Lat(10.0,10.1)", "SUBSET")]
    [InlineData("&SUBSET=Long(-122.45)&SUBSET=Lat(10.0,10.1)", "SUBSET")]
    [InlineData("&BBOX=-100.0,10.0,-99.9,10.1", "BBOX")]
    [InlineData("&SUBSET=x(0,1000)&SUBSET=y(0,1000)&SUBSETTINGCRS=EPSG:3857", "SUBSET")]
    [Operation(Operations.ErrorHandling)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_SpatialTrimOutsideCoverage_ReturnsInvalidSubsettingWithoutReading(
        string spatialTrim, string locator)
    {
        using var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0&FORMAT=image/tiff{spatialTrim}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, content);
        content.Should().Contain("exceptionCode=\"InvalidSubsetting\"");
        content.Should().Contain($"locator=\"{locator}\"");
        _exportQueries.Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("&SUBSET=Long(-122.36,-122.2)&SUBSET=Lat(37.8,37.9)")]
    [InlineData("&SUBSET=Long(-122.45)&SUBSET=Lat(37.75,37.8)")]
    [InlineData("&BBOX=-122.36,37.8,-122.2,37.9")]
    [Operation(Operations.Export)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_SpatialTrimOverlappingCoverageEdge_IsServed(string spatialTrim)
    {
        using var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0&FORMAT=image/png{spatialTrim}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        _exportQueries.Should().ContainSingle();
    }
}

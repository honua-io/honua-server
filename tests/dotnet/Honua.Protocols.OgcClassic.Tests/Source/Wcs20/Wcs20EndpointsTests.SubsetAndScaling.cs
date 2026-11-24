// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

public sealed partial class Wcs20EndpointsTests
{
    [IntegrationTheory]
    [InlineData("x(0,99),y(0,199)", 100, 200)]
    [InlineData("x(4,4),y(7,7)", 1, 1)]
    [Operation(Operations.Export)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_WithScaleExtent_SetsOutputGridSpan(string extent, int width, int height)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0&FORMAT=image/tiff&SCALEEXTENT={extent}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _exportQueries.Should().ContainSingle();
        var query = _exportQueries.Single();
        query.OutputWidth.Should().Be(width);
        query.OutputHeight.Should().Be(height);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_WithOpenSpatialTrim_UsesCoverageEdge()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0&FORMAT=image/tiff&SUBSET=Long(*,-122.4)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var query = _exportQueries.Should().ContainSingle().Which;
        query.ClipRegion.Should().NotBeNull();
        var bounds = new WKBReader().Read(query.ClipRegion!.Value.Geometry).EnvelopeInternal;
        bounds.MinX.Should().BeApproximately(-122.5, 1e-8);
        bounds.MaxX.Should().BeApproximately(-122.4, 1e-8);
    }

    [IntegrationTheory]
    [InlineData("SCALEAXES", "z(2)")]
    [InlineData("SCALESIZE", "z(10)")]
    [InlineData("SCALEEXTENT", "z(0,9)")]
    [Operation(Operations.ErrorHandling)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_WithUnknownScalingAxis_ReturnsScaleAxisUndefined(string parameter, string value)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0&FORMAT=image/tiff&{parameter}={value}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, content);
        content.Should().Contain("exceptionCode=\"ScaleAxisUndefined\"").And.Contain($"locator=\"{parameter}\"");
        _exportQueries.Should().BeEmpty();
    }
}

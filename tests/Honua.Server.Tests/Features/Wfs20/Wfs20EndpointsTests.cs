// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Wfs20;

[Collection("Database")]
[Protocol(Protocols.Wfs20)]
public sealed class Wfs20EndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    public async Task Wfs_GetCapabilities_ReturnsXmlWithoutTransactionOperation()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("WFS_Capabilities");
        content.Should().Contain("<Operation name=\"GetFeature\">");
        content.Should().Contain("<Operation name=\"GetPropertyValue\">");
        content.Should().NotContain("<Operation name=\"Transaction\">");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs_GetCapabilities_InvalidAcceptVersions_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&ACCEPTVERSIONS=1.1.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"VersionNegotiationFailed\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs_DescribeFeatureType_UnsupportedOutputFormat_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=DescribeFeatureType&VERSION=2.0.0&OUTPUTFORMAT=application/json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("DescribeFeatureType requires XML-based formats");
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed partial class Wfs20EndpointsTests
{
    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeatureById_NonNumericQualifiedId_ReturnsInvalidParameterValue()
    {
        const string featureId = "pub-layer-0.abc";
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&STOREDQUERY_ID={Uri.EscapeDataString(GetFeatureByIdStoredQueryId)}&ID={Uri.EscapeDataString(featureId)}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "DescribeStoredQueries")]
    public async Task Wfs_DescribeStoredQueries_UnknownId_ReturnsInvalidParameterValue()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=DescribeStoredQueries&VERSION=2.0.0&STOREDQUERY_ID=urn:honua:test:no-such-query");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"storedquery_id\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "DropStoredQuery")]
    public async Task Wfs_DropStoredQuery_MissingId_ReturnsMissingParameterValue()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=DropStoredQuery&VERSION=2.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"MissingParameterValue\"");
    }
}

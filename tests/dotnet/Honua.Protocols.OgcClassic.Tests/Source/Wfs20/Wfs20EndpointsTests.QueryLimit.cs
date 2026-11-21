// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed partial class Wfs20EndpointsTests
{
    // Above the default Limits:Query:MaxMapLayersPerRequest (50) and the published type count.
    private const int OverLimitQueries = 120;

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_Post_GetFeature_OverTheQueryLimit_ReturnsExceptionReport()
    {
        var queries = string.Concat(Enumerable.Repeat("""<wfs:Query typeNames="pub-layer-0" />""", OverLimitQueries));
        var requestBody = $"""
            <wfs:GetFeature service="WFS" version="2.0.0" xmlns:wfs="http://www.opengis.net/wfs/2.0">{queries}</wfs:GetFeature>
            """;

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", content);

        await AssertQueryLimitExceptionAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_KvpMultiQueryOverTheQueryLimit_ReturnsExceptionReport()
    {
        var typeNames = string.Join(',', Enumerable.Repeat("pub-layer-0", OverLimitQueries));
        var filters = Uri.EscapeDataString(string.Join(';', Enumerable.Repeat("name='missing'", OverLimitQueries)));

        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES={typeNames}&FILTER={filters}");

        await AssertQueryLimitExceptionAsync(response);
    }

    private static async Task AssertQueryLimitExceptionAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        body.Should().Contain("GetFeature may name at most");
    }
}

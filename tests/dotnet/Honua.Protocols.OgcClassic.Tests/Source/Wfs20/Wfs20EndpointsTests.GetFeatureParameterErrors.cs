// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed partial class Wfs20EndpointsTests
{
    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_WithoutTypeNamesOrResourceId_ReturnsMissingParameterValue()
    {
        var response = await _fixture.Client.GetAsync("/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&COUNT=1");

        await AssertMissingTypeNamesAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_Post_GetFeature_WithoutQuery_ReturnsMissingParameterValue()
    {
        using var content = new StringContent(
            """<wfs:GetFeature service="WFS" version="2.0.0" count="1" xmlns:wfs="http://www.opengis.net/wfs/2.0" />""",
            Encoding.UTF8,
            "application/xml");

        var response = await _fixture.Client.PostAsync("/wfs", content);

        await AssertMissingTypeNamesAsync(response);
    }

    private static async Task AssertMissingTypeNamesAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"MissingParameterValue\"");
        content.Should().Contain("locator=\"typeNames\"");
    }
}

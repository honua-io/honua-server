// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wps20;

[Collection("Database")]
[Protocol(TestProtocols.Wps202)]
public sealed class Wps20EndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wps")]
    [InterfaceOperation(TestProtocols.Wps202, "GetCapabilities")]
    public async Task GetCapabilities_AdvertisesOnlyImplementedAsyncOperations()
    {
        var response = await _fixture.Client.GetAsync("/wps?service=WPS&request=GetCapabilities&version=2.0.0");
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, xml);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        xml.Should().Contain("<wps:Capabilities").And.Contain("jobControlOptions=\"async-execute\"");
        xml.Should().NotContain("Operation name=\"Dismiss\"").And.NotContain("sync-execute");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /wps")]
    [InterfaceOperation(TestProtocols.Wps202, "DescribeProcess")]
    public async Task DescribeProcess_Kvp_ReturnsNamespaceCorrectDescription()
    {
        var response = await _fixture.Client.GetAsync("/wps?service=WPS&request=DescribeProcess&version=2.0.0&identifier=geometry.buffer");
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, xml);
        xml.Should().Contain($"xmlns:wps=\"http://www.opengis.net/wps/2.0\"");
        xml.Should().Contain("<ows:Identifier>geometry.buffer</ows:Identifier>");
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("POST /wps")]
    [InterfaceOperation(TestProtocols.Wps202, "Execute")]
    public async Task Execute_XmlWithDtd_IsRejectedWithoutEntityExpansion()
    {
        const string body = "<!DOCTYPE x [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><wps:Execute xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0'><ows:Identifier>&xxe;</ows:Identifier></wps:Execute>";
        using var content = new StringContent(body, Encoding.UTF8, "application/xml");

        var response = await _fixture.Client.PostAsync("/wps", content);
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, xml);
        xml.Should().Contain("ExceptionReport").And.Contain("prohibited constructs");
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /wps")]
    [InterfaceOperation(TestProtocols.Wps202, "GetStatus")]
    public async Task GetStatus_UnknownJob_ReturnsOwsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync("/wps?service=WPS&request=GetStatus&version=2.0.0&jobId=missing-job");
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, xml);
        xml.Should().Contain("exceptionCode=\"NoSuchJob\"");
        xml.Should().Contain("xmlns:ows=\"http://www.opengis.net/ows/2.0\"");
    }
}

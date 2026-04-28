// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Collection("Database")]
public sealed class WfsLegacyEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs11)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs11, "GetCapabilities")]
    public async Task Wfs11_GetCapabilities_ReturnsOws10Capabilities()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=1.1.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("<wfs:WFS_Capabilities");
        content.Should().Contain("version=\"1.1.0\"");
        content.Should().Contain("xmlns:ows=\"http://www.opengis.net/ows\"");
        content.Should().Contain("<ows:Operation name=\"GetFeature\">");
        content.Should().Contain("<wfs:DefaultSRS>");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs11)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs11, "GetCapabilities")]
    public async Task Wfs11_GetCapabilities_AcceptVersionsClientOrder_ReturnsWfs11()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&ACCEPTVERSIONS=1.1.0,2.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<wfs:WFS_Capabilities");
        content.Should().Contain("version=\"1.1.0\"");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs11)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs11, "DescribeFeatureType")]
    public async Task Wfs11_DescribeFeatureType_ReturnsGml31Schema()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=DescribeFeatureType&VERSION=1.1.0&TYPENAME=test_layer");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("xmlns:gml=\"http://www.opengis.net/gml\"");
        content.Should().Contain("http://schemas.opengis.net/gml/3.1.1/base/gml.xsd");
        content.Should().Contain("name=\"test_layer\"");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs11)]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs11, "GetFeature")]
    public async Task Wfs11_GetFeature_ReturnsGml31FeatureCollection()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=1.1.0&TYPENAME=test_layer&MAXFEATURES=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/gml+xml");
        content.Should().Contain("<wfs:FeatureCollection");
        content.Should().Contain("version=\"1.1.0\"");
        content.Should().Contain("xmlns:gml=\"http://www.opengis.net/gml\"");
        content.Should().Contain("<gml:featureMember>");
        content.Should().Contain("gml:id=\"test_layer.");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs11)]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs11, "GetFeature")]
    public async Task Wfs11_GetFeature_TextXmlGml31SubtypeOutputFormat_ReturnsGml31FeatureCollection()
    {
        var outputFormat = Uri.EscapeDataString("text/xml; subtype=gml/3.1.1");
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=1.1.0&TYPENAME=test_layer&OUTPUTFORMAT={outputFormat}&MAXFEATURES=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/gml+xml");
        content.Should().Contain("<wfs:FeatureCollection");
        content.Should().Contain("version=\"1.1.0\"");
        content.Should().Contain("gml:id=\"test_layer.");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs11)]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs11, "GetFeature")]
    public async Task Wfs11_GetFeature_MaxFeaturesZero_ReportsNoReturnedFeatures()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=1.1.0&TYPENAME=test_layer&MAXFEATURES=0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("numberOfFeatures=\"0\"");
        content.Should().NotContain("<gml:featureMember>");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs11)]
    [Operation(Operations.Query)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs11, "GetFeature")]
    public async Task Wfs11_Post_GetFeature_XmlBody_ReturnsGml31FeatureCollection()
    {
        const string body = """
            <wfs:GetFeature service="WFS" version="1.1.0" maxFeatures="1"
                xmlns:wfs="http://www.opengis.net/wfs">
              <wfs:Query typeName="test_layer" />
            </wfs:GetFeature>
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/xml");

        var response = await _fixture.Client.PostAsync("/wfs", content);

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);
        responseBody.Should().Contain("<wfs:FeatureCollection");
        responseBody.Should().Contain("version=\"1.1.0\"");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs11)]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs11, "GetFeature")]
    public async Task Wfs11_GetFeature_LegacySpatialFilters_ReturnMatchingFeature()
    {
        const string bboxFilter = """
            <ogc:Filter xmlns:ogc="http://www.opengis.net/ogc" xmlns:gml="http://www.opengis.net/gml">
              <ogc:BBOX>
                <ogc:PropertyName>shape</ogc:PropertyName>
                <gml:Envelope srsName="CRS84">
                  <gml:lowerCorner>-122.35 37.75</gml:lowerCorner>
                  <gml:upperCorner>-122.25 37.85</gml:upperCorner>
                </gml:Envelope>
              </ogc:BBOX>
            </ogc:Filter>
            """;
        const string intersectsFilter = """
            <ogc:Filter xmlns:ogc="http://www.opengis.net/ogc" xmlns:gml="http://www.opengis.net/gml">
              <ogc:Intersects>
                <ogc:PropertyName>shape</ogc:PropertyName>
                <gml:Point srsName="CRS84">
                  <gml:pos>-122.3 37.8</gml:pos>
                </gml:Point>
              </ogc:Intersects>
            </ogc:Filter>
            """;

        await AssertLegacyFilterReturnsFifthFeatureAsync("1.1.0", bboxFilter);
        await AssertLegacyFilterReturnsFifthFeatureAsync("1.1.0", intersectsFilter);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs11)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs11_GetCapabilities_InvalidVersion_ReturnsOws10ExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=1.2.0&ACCEPTVERSIONS=1.1.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("<ows:ExceptionReport xmlns:ows=\"http://www.opengis.net/ows\" version=\"1.0.0\">");
        content.Should().Contain("VersionNegotiationFailed");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs11)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs11_GetFeature_UnknownType_ReturnsOws10ExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=1.1.0&TYPENAME=missing_layer");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("InvalidParameterValue");
        content.Should().Contain("Unknown feature type");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs11)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs11_GetFeature_MalformedFeatureId_ReturnsOws10ExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=1.1.0&TYPENAME=test_layer&FEATUREID=test_layer.bad");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("<ows:ExceptionReport xmlns:ows=\"http://www.opengis.net/ows\" version=\"1.0.0\">");
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"RESOURCEID\"");
        content.Should().Contain("resourceId");
        content.Should().Contain("test_layer.bad");
        content.Should().Contain("is malformed.");
        content.Should().NotContain("NoApplicableCode");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs11)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs11_GetFeature_UnsupportedSrsName_ReturnsOws10ExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=1.1.0&TYPENAME=test_layer&SRSNAME=EPSG:999999");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("<ows:ExceptionReport xmlns:ows=\"http://www.opengis.net/ows\" version=\"1.0.0\">");
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"SRSNAME\"");
        content.Should().Contain("Unsupported srsName");
        content.Should().Contain("EPSG:999999");
        content.Should().NotContain("NoApplicableCode");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs10)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs10, "GetCapabilities")]
    public async Task Wfs10_GetCapabilities_ReturnsLegacyCapabilities()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=1.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<WFS_Capabilities");
        content.Should().Contain("version=\"1.0.0\"");
        content.Should().Contain("http://schemas.opengis.net/wfs/1.0.0/WFS-capabilities.xsd");
        content.Should().NotContain("http://schemas.opengis.net/wfs/1.0.0/WFS-basic.xsd");
        content.Should().Contain("<FeatureTypeList>");
        content.Should().Contain("<SRS>EPSG:");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs10)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs10, "GetCapabilities")]
    public async Task Wfs10_GetCapabilities_AcceptVersionsClientOrder_ReturnsWfs10()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&ACCEPTVERSIONS=1.0.0,1.1.0,2.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<WFS_Capabilities");
        content.Should().Contain("version=\"1.0.0\"");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs10)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs10, "DescribeFeatureType")]
    public async Task Wfs10_DescribeFeatureType_ReturnsGml2Schema()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=DescribeFeatureType&VERSION=1.0.0&TYPENAME=test_layer");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("xmlns:gml=\"http://www.opengis.net/gml\"");
        content.Should().Contain("http://schemas.opengis.net/gml/2.1.2/feature.xsd");
        content.Should().Contain("name=\"test_layer\"");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs10)]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs10, "GetFeature")]
    public async Task Wfs10_GetFeature_ReturnsGml2FeatureCollection()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=1.0.0&TYPENAME=test_layer&MAXFEATURES=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/gml+xml");
        content.Should().Contain("<wfs:FeatureCollection");
        content.Should().Contain("version=\"1.0.0\"");
        content.Should().Contain("<gml:featureMember>");
        content.Should().Contain("<gml:coordinates>");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs10)]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs10, "GetFeature")]
    public async Task Wfs10_GetFeature_TextXmlGml21SubtypeOutputFormat_ReturnsGml2FeatureCollection()
    {
        var outputFormat = Uri.EscapeDataString("text/xml; subtype=gml/2.1.2");
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=1.0.0&TYPENAME=test_layer&OUTPUTFORMAT={outputFormat}&MAXFEATURES=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/gml+xml");
        content.Should().Contain("<wfs:FeatureCollection");
        content.Should().Contain("version=\"1.0.0\"");
        content.Should().Contain("<gml:coordinates>");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs10)]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs10, "GetFeature")]
    public async Task Wfs10_GetFeature_MaxFeaturesZero_ReportsNoReturnedFeatures()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=1.0.0&TYPENAME=test_layer&MAXFEATURES=0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("numberOfFeatures=\"0\"");
        content.Should().NotContain("<gml:featureMember>");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs10)]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs10, "GetFeature")]
    public async Task Wfs10_GetFeature_LegacySpatialFilters_ReturnMatchingFeature()
    {
        const string bboxFilter = """
            <ogc:Filter xmlns:ogc="http://www.opengis.net/ogc" xmlns:gml="http://www.opengis.net/gml">
              <ogc:BBOX>
                <ogc:PropertyName>shape</ogc:PropertyName>
                <gml:Box srsName="CRS84">
                  <gml:coordinates>-122.35,37.75 -122.25,37.85</gml:coordinates>
                </gml:Box>
              </ogc:BBOX>
            </ogc:Filter>
            """;
        const string intersectsFilter = """
            <ogc:Filter xmlns:ogc="http://www.opengis.net/ogc" xmlns:gml="http://www.opengis.net/gml">
              <ogc:Intersects>
                <ogc:PropertyName>shape</ogc:PropertyName>
                <gml:Point srsName="CRS84">
                  <gml:coordinates>-122.3,37.8</gml:coordinates>
                </gml:Point>
              </ogc:Intersects>
            </ogc:Filter>
            """;

        await AssertLegacyFilterReturnsFifthFeatureAsync("1.0.0", bboxFilter);
        await AssertLegacyFilterReturnsFifthFeatureAsync("1.0.0", intersectsFilter);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs10)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs10_GetCapabilities_InvalidVersion_ReturnsServiceExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=1.2.0&ACCEPTVERSIONS=1.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("<ServiceExceptionReport version=\"1.0.0\">");
        content.Should().Contain("VersionNegotiationFailed");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs10)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs10_GetFeature_UnknownType_ReturnsServiceExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=1.0.0&TYPENAME=missing_layer");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("<ServiceExceptionReport version=\"1.0.0\">");
        content.Should().Contain("Unknown feature type");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs10)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs10_GetFeature_MalformedFeatureId_ReturnsServiceExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=1.0.0&TYPENAME=test_layer&FEATUREID=test_layer.bad");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("<ServiceExceptionReport version=\"1.0.0\">");
        content.Should().Contain("code=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"RESOURCEID\"");
        content.Should().Contain("resourceId");
        content.Should().Contain("test_layer.bad");
        content.Should().Contain("is malformed.");
        content.Should().NotContain("NoApplicableCode");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs10)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs10_GetFeature_UnsupportedSrsName_ReturnsServiceExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=1.0.0&TYPENAME=test_layer&SRSNAME=EPSG:999999");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("<ServiceExceptionReport version=\"1.0.0\">");
        content.Should().Contain("code=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"SRSNAME\"");
        content.Should().Contain("Unsupported srsName");
        content.Should().Contain("EPSG:999999");
        content.Should().NotContain("NoApplicableCode");
    }

    private async Task AssertLegacyFilterReturnsFifthFeatureAsync(string version, string filter)
    {
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION={version}&TYPENAME=test_layer&MAXFEATURES=5&FILTER={Uri.EscapeDataString(filter)}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("Fifth Feature");
        content.Should().NotContain("Another Feature");
    }
}

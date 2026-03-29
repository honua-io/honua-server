// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using System.Text.Json;

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
    [InterfaceOperation(Protocols.Wfs20, "GetCapabilities")]
    public async Task Wfs_GetCapabilities_ReturnsXmlWithoutTransactionOperation()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("WFS_Capabilities");
        content.Should().Contain("updateSequence=\"20260325\"");
        content.Should().Contain("<Operation name=\"GetFeature\">");
        content.Should().Contain("<Operation name=\"GetPropertyValue\">");
        content.Should().NotContain("<Operation name=\"Transaction\">");
        content.Should().Contain("name=\"ImplementsBasicWFS\"");
        content.Should().Contain("name=\"KVPEncoding\"");
        content.Should().Contain("name=\"XMLEncoding\"");
        content.Should().Contain("ImplementsSpatialFilter");
        content.Should().Contain(">TRUE<");
        content.Should().Contain("SpatialOperator name=\"Intersects\"");
        content.Should().Contain("SpatialOperator name=\"DWithin\"");
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
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetCapabilities")]
    public async Task Wfs_GetCapabilities_SectionsFeatureTypeList_ReturnsRequestedSectionOnly()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0&SECTIONS=FeatureTypeList");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<FeatureTypeList>");
        content.Should().NotContain("<ows:ServiceIdentification>");
        content.Should().NotContain("<ows:ServiceProvider>");
        content.Should().NotContain("<ows:OperationsMetadata>");
        content.Should().NotContain("Filter_Capabilities");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs_GetCapabilities_UpdateSequenceHigher_ReturnsInvalidUpdateSequence()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0&UPDATESEQUENCE=999999999");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidUpdateSequence\"");
        content.Should().Contain("locator=\"updateSequence\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "DescribeFeatureType")]
    public async Task Wfs_DescribeFeatureType_UnsupportedOutputFormat_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=DescribeFeatureType&VERSION=2.0.0&OUTPUTFORMAT=application/json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"outputFormat\"");
        content.Should().Contain("DescribeFeatureType requires XML-based formats");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_GeoJsonOutput_ReturnsFeatureCollection()
    {
        const string outputFormat = "application/geo%2Bjson";
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer&OUTPUTFORMAT={outputFormat}&COUNT=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        var features = document.RootElement.GetProperty("features");
        features.ValueKind.Should().Be(JsonValueKind.Array);
        document.RootElement.GetProperty("numberReturned").GetInt32().Should().BeLessOrEqualTo(1);

        var feature = features.EnumerateArray().Single();
        feature.GetProperty("id").GetString().Should().StartWith("test_layer.");
        feature.GetProperty("properties").TryGetProperty("id", out _).Should().BeFalse();
        feature.GetProperty("properties").TryGetProperty("objectid", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_LegacyTypeNameAndMaxFeaturesAliases_ReturnFeatureCollection()
    {
        const string outputFormat = "application/geo%2Bjson";
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAME=test_layer&OUTPUTFORMAT={outputFormat}&MAXFEATURES=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        document.RootElement.GetProperty("features").ValueKind.Should().Be(JsonValueKind.Array);
        document.RootElement.GetProperty("numberReturned").GetInt32().Should().BeLessOrEqualTo(1);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_ResultTypeHits_ReturnsCountWithoutFeatures()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAME=test_layer&RESULTTYPE=hits");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/gml+xml");
        content.Should().Contain("FeatureCollection");
        content.Should().Contain("numberReturned=\"0\"");
        content.Should().Contain("numberMatched=");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_DefaultGmlOutput_ReturnsFeatureCollection()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAME=test_layer&COUNT=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/gml+xml");
        content.Should().Contain("FeatureCollection");
        content.Should().Contain("test_layer");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_Gml4326Output_UsesLatitudeLongitudeCoordinates()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAME=test_layer&COUNT=1&SRSNAME=urn:ogc:def:crs:EPSG::4326");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/gml+xml");
        content.Should().Contain("37.5 -122.5");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_InvalidCount_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAME=test_layer&COUNT=abc");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"count\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_InvalidStartIndex_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAME=test_layer&STARTINDEX=-1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"startIndex\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_InvalidResultType_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAME=test_layer&RESULTTYPE=summary");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"resultType\"");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetPropertyValue")]
    public async Task Wfs_GetPropertyValue_MissingValueReference_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetPropertyValue&VERSION=2.0.0&TYPENAMES=test_layer");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("exceptionCode=\"MissingParameterValue\"");
        content.Should().Contain("locator=\"valueReference\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs_MissingRequestParam_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&VERSION=2.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"MissingParameterValue\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs_UnsupportedOperation_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=ListStoredQueries&VERSION=2.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs_InvalidServiceParam_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=2.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs_Transaction_ReturnsNotImplementedExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=Transaction&VERSION=2.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"OperationNotSupported\"");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetCapabilities")]
    public async Task Wfs_Post_GetCapabilities_ReturnsXml()
    {
        // WFS dispatcher binds parameters from query string; POST body is for XML payloads.
        var response = await _fixture.Client.PostAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0", null);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("WFS_Capabilities");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetCapabilities")]
    public async Task Wfs_Post_GetCapabilities_XmlBody_ReturnsXml()
    {
        const string requestBody = """
            <wfs:GetCapabilities service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0" />
            """;

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", content);

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);
        responseBody.Should().Contain("WFS_Capabilities");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetPropertyValue")]
    public async Task Wfs_GetPropertyValue_WithGeoJsonOutput_ReturnsFeatureCollection()
    {
        const string outputFormat = "application/geo%2Bjson";
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetPropertyValue&VERSION=2.0.0&TYPENAMES=test_layer&VALUEREFERENCE=name&OUTPUTFORMAT={outputFormat}&COUNT=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        var features = document.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeLessOrEqualTo(1);

        if (features.GetArrayLength() > 0)
        {
            var feature = features[0];
            feature.GetProperty("id").GetString().Should().StartWith("test_layer.");
            var properties = feature.GetProperty("properties");
            properties.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
            properties.TryGetProperty("id", out _).Should().BeFalse();
            properties.TryGetProperty("objectid", out _).Should().BeFalse();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetPropertyValue")]
    public async Task Wfs_Post_GetPropertyValue_XmlBody_ReturnsValueCollection()
    {
        const string requestBody = """
            <wfs:GetPropertyValue service="WFS" version="2.0.0" valueReference="name"
                xmlns:wfs="http://www.opengis.net/wfs/2.0">
              <wfs:Query typeNames="test_layer" />
            </wfs:GetPropertyValue>
            """;

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", content);

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/gml+xml");
        responseBody.Should().Contain("ValueCollection");
    }
}

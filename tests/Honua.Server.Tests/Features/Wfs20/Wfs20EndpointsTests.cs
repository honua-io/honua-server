// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
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
        document.RootElement.GetProperty("features").ValueKind.Should().Be(JsonValueKind.Array);
        document.RootElement.GetProperty("numberReturned").GetInt32().Should().BeLessOrEqualTo(1);
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
    [InterfaceOperation(Protocols.Wfs20, "GetPropertyValue")]
    public async Task Wfs_GetPropertyValue_MissingValueReference_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetPropertyValue&VERSION=2.0.0&TYPENAMES=test_layer");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("valueReference");
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
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20EndpointsTests : IAsyncLifetime
{
    private const string GetFeatureByIdStoredQueryId = "urn:ogc:def:query:OGC-WFS::GetFeatureById";
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task Wfs_GetCapabilities_ReturnsXmlWithTransactionOperation()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("WFS_Capabilities");
        content.Should().Contain("updateSequence=\"20260325\"");
        content.Should().Contain("xmlns:honua=\"http://honua.io/wfs\"");
        content.Should().Contain("Operation name=\"GetFeature\"");
        content.Should().Contain("Operation name=\"GetPropertyValue\"");
        content.Should().Contain("Operation name=\"Transaction\"");
        content.Should().Contain("Operation name=\"ListStoredQueries\"");
        content.Should().Contain("Operation name=\"DescribeStoredQueries\"");
        content.Should().Contain("name=\"ImplementsBasicWFS\"");
        content.Should().Contain("name=\"ImplementsTransactionalWFS\"");
        content.Should().Contain("name=\"KVPEncoding\"");
        content.Should().Contain("name=\"XMLEncoding\"");
        content.Should().Contain("ImplementsSpatialFilter");
        Regex.IsMatch(
            content,
            "name=\"ImplementsTransactionalWFS\"[\\s\\S]*?<[^>]*DefaultValue>TRUE</",
            RegexOptions.CultureInvariant).Should().BeTrue(content);
        content.Should().Contain(">TRUE<");
        content.Should().Contain("SpatialOperator name=\"Intersects\"");
        content.Should().Contain("SpatialOperator name=\"DWithin\"");
        Regex.IsMatch(
            content,
            "name=\"ImplementsResultPaging\"[\\s\\S]*?<[^>]*DefaultValue>TRUE</",
            RegexOptions.CultureInvariant).Should().BeTrue(content);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task Wfs_GetCapabilities_WithoutServiceParameter_ReturnsXml()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?REQUEST=GetCapabilities&VERSION=2.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("WFS_Capabilities");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task Wfs_GetCapabilities_WithProjectedExtent_UsesTransformFallbackForWgs84BoundingBox()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILayerCatalog>(new ProjectedExtentLayerCatalog());

        try
        {
            await fixture.InitializeAsync();

            var response = await fixture.Client.GetAsync(
                "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0");

            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, content);
            content.Should().Contain("<ows:WGS84BoundingBox");
            content.Should().Contain("<ows:LowerCorner>-");
            content.Should().Contain("<ows:UpperCorner>-");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
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
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task Wfs_GetCapabilities_WithExplicitlyRejectedXmlAccept_ReturnsNotAcceptable()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/json;q=1, application/xml;q=0");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task Wfs_GetCapabilities_WithJsonAccept_ReturnsXml()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task Wfs_GetCapabilities_WithXmlPreferredOverRejectedWildcard_ReturnsXml()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/xml;q=0.5, */*;q=0");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "DescribeFeatureType")]
    public async Task Wfs_DescribeFeatureType_WithExplicitlyRejectedXmlAccept_ReturnsNotAcceptable()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/wfs?SERVICE=WFS&REQUEST=DescribeFeatureType&VERSION=2.0.0&TYPENAMES=test_layer");
        request.Headers.TryAddWithoutValidation("Accept", "application/json;q=1, application/xml;q=0");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
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
    public async Task Wfs_GetCapabilities_UpdateSequenceCurrent_ReturnsCurrentUpdateSequence()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0&UPDATESEQUENCE=20260325");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"CurrentUpdateSequence\"");
        content.Should().Contain("locator=\"updateSequence\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs_GetCapabilities_UnsupportedAcceptFormats_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0&ACCEPTFORMATS=application/json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"acceptFormats\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "DescribeFeatureType")]
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
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
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
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
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
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
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
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
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
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
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
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_BboxWithoutSrsName_UsesLayerAxisOrder()
    {
        const string outputFormat = "application/geo%2Bjson";
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer&BBOX=37.7,-122.5,37.8,-122.3&OUTPUTFORMAT={outputFormat}&COUNT=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        using var document = JsonDocument.Parse(content);
        var features = document.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0, content);
        features[0].GetProperty("properties").GetProperty("name").GetString().Should().Be("Fifth Feature");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
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
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
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
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_NotValueReferenceFilter_ReturnsExceptionReport()
    {
        const string notFilter = "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:Not><fes:ValueReference>category</fes:ValueReference></fes:Not></fes:Filter>";
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer&FILTER={Uri.EscapeDataString(notFilter)}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("Invalid WFS parameter value");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetPropertyValue")]
    public async Task Wfs_GetPropertyValue_NotValueReferenceFilter_ReturnsExceptionReport()
    {
        const string notFilter = "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:Not><fes:ValueReference>category</fes:ValueReference></fes:Not></fes:Filter>";
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetPropertyValue&VERSION=2.0.0&TYPENAMES=test_layer&VALUEREFERENCE=name&FILTER={Uri.EscapeDataString(notFilter)}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("Invalid WFS parameter value");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
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
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_UnknownTypeNames_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=missing_layer");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"typeNames\"");
        content.Should().Contain("Unknown feature type");
        content.Should().Contain("missing_layer");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_MultiTypeUnqualifiedResourceId_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer,related_test_layer_1&RESOURCEID=101");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("must be qualified when multiple feature types are requested");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_MixedValidAndMalformedResourceIds_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer&RESOURCEID=test_layer.1,test_layer.bad");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"RESOURCEID\"");
        content.Should().Contain("is malformed");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetPropertyValue")]
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
    [InterfaceOperation(TestProtocols.Wfs20, "GetPropertyValue")]
    public async Task Wfs_GetPropertyValue_UnknownTypeNames_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetPropertyValue&VERSION=2.0.0&TYPENAMES=missing_layer&VALUEREFERENCE=name");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"typeNames\"");
        content.Should().Contain("Unknown feature type");
        content.Should().Contain("missing_layer");
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
            "/wfs?SERVICE=WFS&REQUEST=LockFeature&VERSION=2.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"OperationNotSupported\"");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "ListStoredQueries")]
    public async Task Wfs_ListStoredQueries_ReturnsGetFeatureByIdDefinition()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=ListStoredQueries&VERSION=2.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("ListStoredQueriesResponse");
        content.Should().Contain($"id=\"{GetFeatureByIdStoredQueryId}\"");
        content.Should().Contain("<wfs:ReturnFeatureType>honua:test_layer</wfs:ReturnFeatureType>");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "ListStoredQueries")]
    public async Task Wfs_ListStoredQueries_WithExplicitlyRejectedXmlAccept_ReturnsNotAcceptable()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/wfs?SERVICE=WFS&REQUEST=ListStoredQueries&VERSION=2.0.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/xml;q=0");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "DescribeStoredQueries")]
    public async Task Wfs_DescribeStoredQueries_ReturnsGetFeatureByIdDefinition()
    {
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=DescribeStoredQueries&VERSION=2.0.0&STOREDQUERY_ID={Uri.EscapeDataString(GetFeatureByIdStoredQueryId)}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("DescribeStoredQueriesResponse");
        content.Should().Contain($"StoredQueryDescription id=\"{GetFeatureByIdStoredQueryId}\"");
        content.Should().Contain("Parameter name=\"id\" type=\"xsd:string\"");
        content.Should().Contain("QueryExpressionText");
        content.Should().Contain("rid=\"${id}\"");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "DescribeStoredQueries")]
    public async Task Wfs_DescribeStoredQueries_WithExplicitlyRejectedXmlAccept_ReturnsNotAcceptable()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/wfs?SERVICE=WFS&REQUEST=DescribeStoredQueries&VERSION=2.0.0&STOREDQUERY_ID={Uri.EscapeDataString(GetFeatureByIdStoredQueryId)}");
        request.Headers.TryAddWithoutValidation("Accept", "application/xml;q=0");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
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
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_WithXmlLockId_ReturnsExceptionReport()
    {
        const string requestBody = """
            <wfs:Transaction service="WFS" version="2.0.0" lockId="abc123" releaseAction="ALL"
                xmlns:wfs="http://www.opengis.net/wfs/2.0" />
            """;

        using var requestContent = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", requestContent);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"lockId\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_WithExplicitlyRejectedXmlAccept_ReturnsNotAcceptable()
    {
        const string requestBody = """
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0" />
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/wfs")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/xml")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/xml;q=0");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_Insert_ReturnsTransactionResponseAndCreatesFeature()
    {
        const string requestBody = """
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:gml="http://www.opengis.net/gml/3.2"
                xmlns:honua="http://honua.io/wfs">
              <wfs:Insert handle="insert-feature">
                <honua:test_layer>
                  <honua:name>WFS Transaction Insert</honua:name>
                  <honua:shape>
                    <gml:Point srsName="urn:ogc:def:crs:EPSG::4326">
                      <gml:pos>37.123 -122.456</gml:pos>
                    </gml:Point>
                  </honua:shape>
                </honua:test_layer>
              </wfs:Insert>
            </wfs:Transaction>
            """;

        using var requestContent = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", requestContent);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("TransactionResponse");
        content.Should().Contain("<wfs:totalInserted>1</wfs:totalInserted>");
        content.Should().Contain("handle=\"insert-feature\"");

        var resourceIdMatch = Regex.Match(content, "rid=\"(?<rid>test_layer\\.\\d+)\"", RegexOptions.CultureInvariant);
        resourceIdMatch.Success.Should().BeTrue(content);
        var resourceId = resourceIdMatch.Groups["rid"].Value;

        var queryResponse = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer&RESOURCEID={resourceId}");
        var queryContent = await queryResponse.Content.ReadAsStringAsync();

        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, queryContent);
        queryContent.Should().Contain("WFS Transaction Insert");
        queryContent.Should().Contain(resourceId);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_AnonymousWrite_AllowsInsertWithoutRbac()
    {
        await UpdateLayerMetadataAsync(new CatalogMetadata
        {
            AccessPolicy = new AccessPolicy
            {
                AllowAnonymousWrite = true
            }
        });

        const string requestBody = """
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:gml="http://www.opengis.net/gml/3.2"
                xmlns:honua="http://honua.io/wfs">
              <wfs:Insert handle="anonymous-write">
                <honua:test_layer>
                  <honua:name>WFS Anonymous Write Insert</honua:name>
                  <honua:shape>
                    <gml:Point srsName="urn:ogc:def:crs:EPSG::4326">
                      <gml:pos>37.223 -122.556</gml:pos>
                    </gml:Point>
                  </honua:shape>
                </honua:test_layer>
              </wfs:Insert>
            </wfs:Transaction>
            """;

        using var requestContent = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", requestContent);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("TransactionResponse");
        content.Should().Contain("<wfs:totalInserted>1</wfs:totalInserted>");
        content.Should().Contain("anonymous-write");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /wfs")]
    public async Task Wfs_Transaction_WithInsertUpdateAndDelete_ReturnsTransactionSummary()
    {
        var updateId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "WFS Transaction Update Target");
        var deleteId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "WFS Transaction Delete Target");

        var requestBody = $$"""
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:fes="http://www.opengis.net/fes/2.0"
                xmlns:gml="http://www.opengis.net/gml/3.2"
                xmlns:honua="http://honua.io/wfs">
              <wfs:Insert>
                <honua:test_layer>
                  <honua:name>WFS Transaction Mixed Insert</honua:name>
                  <honua:shape>
                    <gml:Point srsName="urn:ogc:def:crs:EPSG::4326">
                      <gml:pos>37.124 -122.457</gml:pos>
                    </gml:Point>
                  </honua:shape>
                </honua:test_layer>
              </wfs:Insert>
              <wfs:Update typeName="test_layer">
                <wfs:Property>
                  <wfs:ValueReference>name</wfs:ValueReference>
                  <wfs:Value>WFS Transaction Updated</wfs:Value>
                </wfs:Property>
                <fes:Filter>
                  <fes:ResourceId rid="test_layer.{{updateId}}" />
                </fes:Filter>
              </wfs:Update>
              <wfs:Delete typeName="test_layer">
                <fes:Filter>
                  <fes:ResourceId rid="test_layer.{{deleteId}}" />
                </fes:Filter>
              </wfs:Delete>
            </wfs:Transaction>
            """;

        using var requestContent = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", requestContent);

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);
        responseBody.Should().Contain("TransactionResponse");
        responseBody.Should().Contain("<wfs:totalInserted>1</wfs:totalInserted>");
        responseBody.Should().Contain("<wfs:totalUpdated>1</wfs:totalUpdated>");
        responseBody.Should().Contain("<wfs:totalDeleted>1</wfs:totalDeleted>");

        var updatedFeatureResponse = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer&RESOURCEID=test_layer.{updateId}");
        var updatedFeatureBody = await updatedFeatureResponse.Content.ReadAsStringAsync();
        updatedFeatureResponse.StatusCode.Should().Be(HttpStatusCode.OK, updatedFeatureBody);
        updatedFeatureBody.Should().Contain("WFS Transaction Updated");

        var deletedFeatureResponse = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer&RESOURCEID=test_layer.{deleteId}");
        var deletedFeatureBody = await deletedFeatureResponse.Content.ReadAsStringAsync();
        deletedFeatureResponse.StatusCode.Should().Be(HttpStatusCode.OK, deletedFeatureBody);
        deletedFeatureBody.Should().Contain("numberMatched=\"0\"");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_WithNamedFeature_UsesApplicationNameWithoutDuplicateGmlName()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer&COUNT=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<honua:name>Test Feature</honua:name>");
        content.Should().NotContain("<gml:name>Test Feature</gml:name>");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_GetFeatureByIdStoredQuery_Kvp_ReturnsSingleFeatureElement()
    {
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&STOREDQUERY_ID={Uri.EscapeDataString(GetFeatureByIdStoredQueryId)}&ID=test_layer.1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/gml+xml");
        content.Should().Contain("<honua:test_layer");
        content.Should().Contain("gml:id=\"test_layer.1\"");
        content.Should().Contain("<gml:name>Test Feature</gml:name>");
        content.Should().NotContain("FeatureCollection");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_Post_GetFeature_GetFeatureByIdStoredQuery_ReturnsSingleFeatureElement()
    {
        var requestBody = $$"""
            <wfs:GetFeature service="WFS" version="2.0.0" count="1"
                xmlns:wfs="http://www.opengis.net/wfs/2.0">
              <wfs:StoredQuery id="{{GetFeatureByIdStoredQueryId}}">
                <wfs:Parameter name="id">test_layer.1</wfs:Parameter>
              </wfs:StoredQuery>
            </wfs:GetFeature>
            """;

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", content);

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/gml+xml");
        responseBody.Should().Contain("<honua:test_layer");
        responseBody.Should().Contain("gml:id=\"test_layer.1\"");
        responseBody.Should().NotContain("FeatureCollection");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_GetFeatureByIdStoredQuery_UnknownId_Returns404ExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&STOREDQUERY_ID={Uri.EscapeDataString(GetFeatureByIdStoredQueryId)}&ID=test_layer.999999");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"NotFound\"");
        content.Should().Contain("locator=\"id\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    public async Task Wfs_GetFeature_UnknownStoredQuery_ReturnsOperationParsingFailedException()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&STOREDQUERY_ID=urn:ogc:def:query:OGC-WFS::UnknownQuery&ID=test_layer.1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"OperationParsingFailed\"");
        content.Should().Contain("locator=\"storedquery_id\"");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_MultiLayerWithoutRollback_ReturnsTransactionResponseAndCreatesFeatures()
    {
        const string requestBody = """
            <wfs:Transaction service="WFS" version="2.0.0" rollbackOnFailure="false"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:gml="http://www.opengis.net/gml/3.2"
                xmlns:honua="http://honua.io/wfs">
              <wfs:Insert handle="insert-primary">
                <honua:test_layer>
                  <honua:name>WFS Multi Layer Primary Insert</honua:name>
                  <honua:shape>
                    <gml:Point srsName="urn:ogc:def:crs:EPSG::4326">
                      <gml:pos>37.101 -122.401</gml:pos>
                    </gml:Point>
                  </honua:shape>
                </honua:test_layer>
              </wfs:Insert>
              <wfs:Insert handle="insert-related">
                <honua:related_test_layer_1>
                  <honua:name>WFS Multi Layer Related Insert</honua:name>
                  <honua:related_id>1</honua:related_id>
                  <honua:shape>
                    <gml:Point srsName="urn:ogc:def:crs:EPSG::4326">
                      <gml:pos>37.202 -122.502</gml:pos>
                    </gml:Point>
                  </honua:shape>
                </honua:related_test_layer_1>
              </wfs:Insert>
            </wfs:Transaction>
            """;

        using var requestContent = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", requestContent);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("TransactionResponse");
        content.Should().Contain("<wfs:totalInserted>2</wfs:totalInserted>");
        content.Should().Contain("handle=\"insert-primary\"");
        content.Should().Contain("handle=\"insert-related\"");

        var primaryRidMatch = Regex.Match(content, "rid=\"(?<rid>test_layer\\.\\d+)\"", RegexOptions.CultureInvariant);
        primaryRidMatch.Success.Should().BeTrue(content);
        var relatedRidMatch = Regex.Match(content, "rid=\"(?<rid>related_test_layer_1\\.\\d+)\"", RegexOptions.CultureInvariant);
        relatedRidMatch.Success.Should().BeTrue(content);

        var primaryRid = primaryRidMatch.Groups["rid"].Value;
        var relatedRid = relatedRidMatch.Groups["rid"].Value;

        var primaryQueryResponse = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer&RESOURCEID={primaryRid}");
        var primaryQueryContent = await primaryQueryResponse.Content.ReadAsStringAsync();
        primaryQueryResponse.StatusCode.Should().Be(HttpStatusCode.OK, primaryQueryContent);
        primaryQueryContent.Should().Contain("WFS Multi Layer Primary Insert");

        var relatedQueryResponse = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=related_test_layer_1&RESOURCEID={relatedRid}");
        var relatedQueryContent = await relatedQueryResponse.Content.ReadAsStringAsync();
        relatedQueryResponse.StatusCode.Should().Be(HttpStatusCode.OK, relatedQueryContent);
        relatedQueryContent.Should().Contain("WFS Multi Layer Related Insert");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_UpdateGmlNameXPathReference_UpdatesStoredQueryResult()
    {
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "WFS XPath Name Original");

        var requestBody = $$"""
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:fes="http://www.opengis.net/fes/2.0">
              <wfs:Update typeName="test_layer">
                <wfs:Property>
                  <wfs:ValueReference>gml:name[1]</wfs:ValueReference>
                  <wfs:Value>WFS XPath Name Updated</wfs:Value>
                </wfs:Property>
                <fes:Filter>
                  <fes:ResourceId rid="test_layer.{{featureId}}" />
                </fes:Filter>
              </wfs:Update>
            </wfs:Transaction>
            """;

        using var requestContent = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", requestContent);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<wfs:totalUpdated>1</wfs:totalUpdated>");

        var queryResponse = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&STOREDQUERY_ID={Uri.EscapeDataString(GetFeatureByIdStoredQueryId)}&ID=test_layer.{featureId}");
        var queryContent = await queryResponse.Content.ReadAsStringAsync();

        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, queryContent);
        queryContent.Should().Contain("<honua:test_layer");
        queryContent.Should().Contain("<gml:name>WFS XPath Name Updated</gml:name>");
        queryContent.Should().NotContain("FeatureCollection");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_Replace_ReturnsReplaceSummaryAndUpdatesFeature()
    {
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "WFS Replace Original");
        var replacementIdentifier = Guid.NewGuid().ToString();

        var requestBody = $$"""
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:fes="http://www.opengis.net/fes/2.0"
                xmlns:gml="http://www.opengis.net/gml/3.2"
                xmlns:honua="http://honua.io/wfs">
              <wfs:Replace>
                <honua:test_layer gml:id="test_layer.{{featureId}}">
                  <gml:description>WFS Replace Description</gml:description>
                  <gml:identifier>{{replacementIdentifier}}</gml:identifier>
                  <gml:name>WFS Replace Name</gml:name>
                  <honua:name>WFS Replace Name</honua:name>
                </honua:test_layer>
                <fes:Filter>
                  <fes:ResourceId rid="test_layer.{{featureId}}" />
                </fes:Filter>
              </wfs:Replace>
            </wfs:Transaction>
            """;

        using var requestContent = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", requestContent);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<wfs:totalReplaced>1</wfs:totalReplaced>");
        content.Should().Contain("<wfs:ReplaceResults>");
        content.Should().Contain($"rid=\"test_layer.{featureId}\"");

        var queryResponse = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&STOREDQUERY_ID={Uri.EscapeDataString(GetFeatureByIdStoredQueryId)}&ID=test_layer.{featureId}");
        var queryContent = await queryResponse.Content.ReadAsStringAsync();

        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, queryContent);
        queryContent.Should().Contain("<honua:test_layer");
        queryContent.Should().Contain("WFS Replace Name");
        queryContent.Should().Contain("WFS Replace Description");
        queryContent.Should().Contain($"<gml:identifier>{replacementIdentifier}</gml:identifier>");
        queryContent.Should().NotContain("FeatureCollection");
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_Delete_DeletedFeatureStoredQueryReturns404ExceptionReport()
    {
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "WFS Delete Target");

        var requestBody = $$"""
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:fes="http://www.opengis.net/fes/2.0">
              <wfs:Delete typeName="test_layer">
                <fes:Filter>
                  <fes:ResourceId rid="test_layer.{{featureId}}" />
                </fes:Filter>
              </wfs:Delete>
            </wfs:Transaction>
            """;

        using var requestContent = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", requestContent);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<wfs:totalDeleted>1</wfs:totalDeleted>");

        var queryResponse = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&STOREDQUERY_ID={Uri.EscapeDataString(GetFeatureByIdStoredQueryId)}&ID=test_layer.{featureId}");
        var queryContent = await queryResponse.Content.ReadAsStringAsync();

        queryResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, queryContent);
        queryResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        queryContent.Should().Contain("ExceptionReport");
        queryContent.Should().Contain("exceptionCode=\"NotFound\"");
        queryContent.Should().Contain("locator=\"id\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_UpdateBoundedBy_WithInvalidValue_ReturnsInvalidValueException()
    {
        var updateId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "WFS BoundedBy Target");

        var requestBody = $$"""
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:fes="http://www.opengis.net/fes/2.0"
                xmlns:kml="http://www.opengis.net/kml/2.2">
              <wfs:Update typeName="test_layer">
                <wfs:Property>
                  <wfs:ValueReference>gml:boundedBy</wfs:ValueReference>
                  <wfs:Value>
                    <kml:Point>
                      <kml:coordinates>-122.456,37.123</kml:coordinates>
                    </kml:Point>
                  </wfs:Value>
                </wfs:Property>
                <fes:Filter>
                  <fes:ResourceId rid="test_layer.{{updateId}}" />
                </fes:Filter>
              </wfs:Update>
            </wfs:Transaction>
            """;

        using var requestContent = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", requestContent);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("ExceptionReport");
        content.Should().Contain("exceptionCode=\"InvalidValue\"");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
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
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
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
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    public async Task Wfs_Post_InvalidXmlBody_ReturnsSanitizedExceptionReport()
    {
        const string requestBody = """
            <wfs:GetCapabilities service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0">
            """;

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", content);

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, responseBody);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        responseBody.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        responseBody.Should().Contain("Invalid WFS XML request body.");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    public async Task Wfs_Post_XmlBodyWithDtd_ReturnsSanitizedExceptionReport()
    {
        const string requestBody = """
            <!DOCTYPE wfs:GetCapabilities [
              <!ENTITY xxe SYSTEM "file:///etc/passwd">
            ]>
            <wfs:GetCapabilities service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0" />
            """;

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", content);

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, responseBody);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        responseBody.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        responseBody.Should().Contain("Invalid WFS XML request body.");
        responseBody.Should().NotContain("/etc/passwd");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_Post_GetFeature_WithMultipleQueries_ReturnsFeatureCollection()
    {
        const string requestBody = """
            <wfs:GetFeature service="WFS" version="2.0.0" outputFormat="application/geo+json" count="100"
                xmlns:wfs="http://www.opengis.net/wfs/2.0">
              <wfs:Query typeNames="test_layer">
                <wfs:PropertyName>name</wfs:PropertyName>
              </wfs:Query>
              <wfs:Query typeNames="test_layer">
                <wfs:PropertyName>category</wfs:PropertyName>
              </wfs:Query>
            </wfs:GetFeature>
            """;

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", content);

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        using var document = JsonDocument.Parse(responseBody);
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Any(feature =>
        {
            var properties = feature.GetProperty("properties");
            return properties.TryGetProperty("name", out _) &&
                   !properties.TryGetProperty("category", out _);
        }).Should().BeTrue();
        features.Any(feature =>
        {
            var properties = feature.GetProperty("properties");
            return properties.TryGetProperty("category", out _) &&
                   !properties.TryGetProperty("name", out _);
        }).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_Post_GetFeature_QueryWithoutTypeNames_ReturnsExceptionReport()
    {
        const string requestBody = """
            <wfs:GetFeature service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0">
              <wfs:Query />
            </wfs:GetFeature>
            """;

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", content);

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, responseBody);
        responseBody.Should().Contain("exceptionCode=\"MissingParameterValue\"");
        responseBody.Should().Contain("locator=\"typeNames\"");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_Post_GetFeature_WithXmlSortBy_AppliesQuerySortOrder()
    {
        const string requestBody = """
            <wfs:GetFeature service="WFS" version="2.0.0" outputFormat="application/geo+json" count="1"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:fes="http://www.opengis.net/fes/2.0">
              <wfs:Query typeNames="test_layer">
                <fes:SortBy>
                  <fes:SortProperty>
                    <fes:ValueReference>objectid</fes:ValueReference>
                    <fes:SortOrder>DESC</fes:SortOrder>
                  </fes:SortProperty>
                </fes:SortBy>
              </wfs:Query>
            </wfs:GetFeature>
            """;

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", content);

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);
        using var document = JsonDocument.Parse(responseBody);
        var feature = document.RootElement.GetProperty("features").EnumerateArray().Single();
        feature.GetProperty("id").GetString().Should().EndWith(".5");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetPropertyValue")]
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
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetPropertyValue")]
    public async Task Wfs_GetPropertyValue_WithUnsupportedResolve_ReturnsExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetPropertyValue&VERSION=2.0.0&TYPENAMES=test_layer&VALUEREFERENCE=name&RESOLVE=local");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"resolve\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetPropertyValue")]
    public async Task Wfs_Post_GetPropertyValue_WithResolveDepth_ReturnsExceptionReport()
    {
        const string requestBody = """
            <wfs:GetPropertyValue service="WFS" version="2.0.0" valueReference="name" resolveDepth="1"
                xmlns:wfs="http://www.opengis.net/wfs/2.0">
              <wfs:Query typeNames="test_layer" />
            </wfs:GetPropertyValue>
            """;

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", content);

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, responseBody);
        responseBody.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        responseBody.Should().Contain("locator=\"resolveDepth\"");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetPropertyValue")]
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

    private Task UpdateLayerMetadataAsync(CatalogMetadata metadata)
    {
        var updater = _fixture.Services.GetRequiredService<ILayerMetadataUpdater>();
        return updater.UpdateLayerMetadataAsync(WebAppFixture.TestLayerId, metadata);
    }
}

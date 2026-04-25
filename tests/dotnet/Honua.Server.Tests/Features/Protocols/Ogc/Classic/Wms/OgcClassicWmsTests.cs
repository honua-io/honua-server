// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class OgcClassicWmsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_ReturnsXml()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        content.Should().Contain("<WMS_Capabilities");
        content.Should().Contain("<Name>WMS</Name>");
        content.Should().Contain("EPSG:3857");
        content.Should().Contain("EPSG:4326");
        content.Should().Contain("<GetMap>");
        content.Should().Contain("<GetFeatureInfo>");
        content.Should().Contain("schemaLocation");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_WithUnsupportedAcceptAndWildcardFallback_ReturnsXml()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, */*;q=0.1");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    public async Task Wms_GetCapabilities_WithWmsEnabledAndMapServerDisabled_ReturnsXml()
    {
        var updater = _fixture.GetService<IServiceMetadataUpdater>();
        await updater.UpdateServiceMetadataAsync(
            WebAppFixture.TestServiceId,
            new CatalogMetadata
            {
                EnabledProtocols = [ServiceProtocols.Wms]
            });

        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wms?SERVICE=WMS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<WMS_Capabilities");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_UnsupportedVersion_ReturnsServiceException()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.2.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("InvalidParameterValue");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms111)]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms111, "GetCapabilities")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms111_GetCapabilities_ReturnsLegacyXml()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.1.1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        content.Should().Contain("<WMT_MS_Capabilities");
        content.Should().Contain("version=\"1.1.1\"");
        content.Should().Contain("<SRS>EPSG:4326</SRS>");
        content.Should().Contain("<LatLonBoundingBox");
        content.Should().Contain("<BoundingBox SRS=\"EPSG:4326\" minx=\"-180.000000\" miny=\"-90.000000\" maxx=\"180.000000\" maxy=\"90.000000\"");
        content.Should().NotContain("<WMS_Capabilities");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_Epsg4326BoundingBox_UsesLatLonAxisOrder()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<BoundingBox CRS=\"EPSG:4326\" minx=\"-90.000000\" miny=\"-180.000000\" maxx=\"90.000000\" maxy=\"180.000000\"");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_ServiceExtentInWebMercator_ReprojectsGeographicBounds()
    {
        await using (var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE honua.services
                SET service_extent = ST_MakeEnvelope(
                    -20037508.342789244,
                    -20037508.342789244,
                    20037508.342789244,
                    20037508.342789244,
                    3857)
                WHERE service_name = @serviceName;
                """;
            command.Parameters.AddWithValue("serviceName", WebAppFixture.TestServiceId);
            await command.ExecuteNonQueryAsync();
        }

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<southBoundLatitude>-85.051129</southBoundLatitude>");
        content.Should().Contain("<northBoundLatitude>85.051129</northBoundLatitude>");
        content.Should().Contain("<BoundingBox CRS=\"EPSG:4326\" minx=\"-85.051129\" miny=\"-180.000000\" maxx=\"85.051129\" maxy=\"180.000000\"");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    public async Task Wms_OgcAlias_GetCapabilities_ReturnsXml()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wms?SERVICE=WMS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        content.Should().Contain("<WMS_Capabilities");
        content.Should().Contain("<Name>WMS</Name>");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_WithExplicitlyRejectedXmlAccept_ReturnsNotAcceptable()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities");
        request.Headers.TryAddWithoutValidation("Accept", "application/json;q=1, application/xml;q=0");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_WithTextXmlRejectedAndWildcardAllowed_ReturnsNotAcceptable()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities");
        request.Headers.TryAddWithoutValidation("Accept", "text/xml;q=0, */*;q=1");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_DefaultRequest_ReturnsXml()
    {
        // When no REQUEST is specified, should default to GetCapabilities
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<WMS_Capabilities");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_ReturnsImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TRANSPARENT=true");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms111)]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms111, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms111_GetMap_Epsg4326UsesLonLatAxisOrder_ReturnsImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.1.1&BBOX=-180,-90,180,90&WIDTH=256&HEIGHT=256&SRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TRANSPARENT=true");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms111)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms111_GetMap_InvalidBbox_ReturnsLegacyServiceException()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.1.1&BBOX=bad&WIDTH=256&HEIGHT=256&SRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("<ServiceExceptionReport version=\"1.1.1\">");
        content.Should().NotContain("xmlns=\"http://www.opengis.net/ogc\"");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_Epsg4326AxisOrder_ReturnsImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-85,170,85,175&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TRANSPARENT=true");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_Jpeg_ReturnsJpeg()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/jpeg");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_MissingBbox_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&WIDTH=256&HEIGHT=256&CRS=EPSG:4326");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_WithSrs_ReturnsImage()
    {
        // SRS is the legacy parameter name for CRS
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&SRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_InvalidServiceId_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/%20/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_InvalidService_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WFS&REQUEST=GetCapabilities");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_WithLayerSelection_ReturnsImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&FORMAT=image/png&LAYERS={WebAppFixture.TestLayerId}&STYLES=");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_InvalidLayer_ReturnsXmlServiceException()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-180,-90,180,90&WIDTH=256&HEIGHT=256&CRS=CRS:84&FORMAT=image/png&LAYERS=NonExistant&STYLES=");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("LayerNotDefined");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_InvalidLayer_WithSeXmlExceptions_ReturnsRequestedExceptionContentType()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-180,-90,180,90&WIDTH=256&HEIGHT=256&CRS=CRS:84&FORMAT=image/png&LAYERS=NonExistant&STYLES=&EXCEPTIONS=application/vnd.ogc.se_xml");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.ogc.se_xml");
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("LayerNotDefined");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_Crs84MinXGreaterThanMaxX_ReturnsXmlServiceException()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=1,0,0,1&WIDTH=200&HEIGHT=200&CRS=CRS:84&FORMAT=image/png&LAYERS={WebAppFixture.TestLayerId}&STYLES=");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("InvalidParameterValue");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_BboxOutsideCrsBounds_ReturnsBlankImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-10,90,10,110&WIDTH=100&HEIGHT=100&CRS=CRS:84&FORMAT=image/png&LAYERS={WebAppFixture.TestLayerId}&STYLES=");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

        using var bitmap = SKBitmap.Decode(content);
        bitmap.Should().NotBeNull();

        for (var y = 0; y < bitmap!.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                bitmap.GetPixel(x, y).Should().Be(SKColors.White);
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetFeatureInfo_ReturnsPlainText()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetFeatureInfo&VERSION=1.3.0&BBOX=-180,-90,180,90&CRS=CRS:84&WIDTH=256&HEIGHT=256&LAYERS={WebAppFixture.TestLayerId}&QUERY_LAYERS={WebAppFixture.TestLayerId}&INFO_FORMAT=text/plain&I=41&J=74");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");
        content.Should().Contain("Layer=");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms111)]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms111, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms111_GetFeatureInfo_UsesXY_ReturnsPlainText()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetFeatureInfo&VERSION=1.1.1&BBOX=-180,-90,180,90&SRS=EPSG:4326&WIDTH=256&HEIGHT=256&LAYERS={WebAppFixture.TestLayerId}&QUERY_LAYERS={WebAppFixture.TestLayerId}&INFO_FORMAT=text/plain&X=41&Y=74");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");
        content.Should().Contain("Layer=");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetFeatureInfo_FiltersInternalAttributes()
    {
        await using (var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE features
                SET attributes = COALESCE(attributes, '{}'::jsonb) || jsonb_build_object('__tenant_id', 'hidden')
                WHERE layer_id = @layerId;
                """;
            command.Parameters.Add(new NpgsqlParameter { ParameterName = "layerId", Value = WebAppFixture.TestLayerId });
            await command.ExecuteNonQueryAsync();
        }

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetFeatureInfo&VERSION=1.3.0&BBOX=-180,-90,180,90&CRS=CRS:84&WIDTH=256&HEIGHT=256&LAYERS={WebAppFixture.TestLayerId}&QUERY_LAYERS={WebAppFixture.TestLayerId}&INFO_FORMAT=application/json&I=41&J=74");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("attributes");
        content.Should().NotContain("__tenant_id");
        content.Should().NotContain("hidden");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetFeatureInfo_WithFilter_AppliesLayerFilter()
    {
        const string noMatchFilter = "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsEqualTo><fes:ValueReference>category</fes:ValueReference><fes:Literal>does-not-exist</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>";
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetFeatureInfo&VERSION=1.3.0&BBOX=-180,-90,180,90&CRS=CRS:84&WIDTH=256&HEIGHT=256&LAYERS={WebAppFixture.TestLayerId}&QUERY_LAYERS={WebAppFixture.TestLayerId}&INFO_FORMAT=text/plain&I=41&J=74&FILTER={Uri.EscapeDataString(noMatchFilter)}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Be("No features found.");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_WithFilter_ReturnsDistinctImage()
    {
        var baseUrl = $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TRANSPARENT=true";

        var noFilterResponse = await _fixture.Client.GetAsync(baseUrl);
        var noFilterBytes = await noFilterResponse.Content.ReadAsByteArrayAsync();
        noFilterResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(noFilterBytes)}");

        const string equalityFilter = "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsEqualTo><fes:ValueReference>category</fes:ValueReference><fes:Literal>test</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>";
        var filteredResponse = await _fixture.Client.GetAsync($"{baseUrl}&FILTER={Uri.EscapeDataString(equalityFilter)}");
        var filteredBytes = await filteredResponse.Content.ReadAsByteArrayAsync();
        filteredResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(filteredBytes)}");
        filteredResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

        var noFilterHash = Convert.ToHexString(SHA256.HashData(noFilterBytes));
        var filteredHash = Convert.ToHexString(SHA256.HashData(filteredBytes));
        filteredHash.Should().NotBe(noFilterHash, "FILTER should produce a distinct raster image");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_DifferentFilters_ReturnDistinctImages()
    {
        var baseUrl = $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TRANSPARENT=true";

        const string equalityFilter = "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsEqualTo><fes:ValueReference>category</fes:ValueReference><fes:Literal>test</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>";
        const string sampleFilter = "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsEqualTo><fes:ValueReference>category</fes:ValueReference><fes:Literal>sample</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>";

        var testResponse = await _fixture.Client.GetAsync($"{baseUrl}&FILTER={Uri.EscapeDataString(equalityFilter)}");
        var testBytes = await testResponse.Content.ReadAsByteArrayAsync();
        testResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var sampleResponse = await _fixture.Client.GetAsync($"{baseUrl}&FILTER={Uri.EscapeDataString(sampleFilter)}");
        var sampleBytes = await sampleResponse.Content.ReadAsByteArrayAsync();
        sampleResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var testHash = Convert.ToHexString(SHA256.HashData(testBytes));
        var sampleHash = Convert.ToHexString(SHA256.HashData(sampleBytes));
        sampleHash.Should().NotBe(testHash, "different FILTER values should produce distinct raster images");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_InvalidFilterXml_ReturnsServiceException()
    {
        const string invalidFilter = "<not-valid-xml>";
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&FILTER={Uri.EscapeDataString(invalidFilter)}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("InvalidParameterValue");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_FilterCountMismatch_ReturnsServiceException()
    {
        const string filter1 = "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsEqualTo><fes:ValueReference>category</fes:ValueReference><fes:Literal>test</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>";
        var twoFilters = Uri.EscapeDataString($"{filter1};{filter1}");

        // Request with 1 layer but 2 filters
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&FILTER={twoFilters}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("InvalidParameterValue");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_ScalarFilterExpression_ReturnsServiceException()
    {
        const string scalarFilter = "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:ValueReference>category</fes:ValueReference></fes:Filter>";
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&FILTER={Uri.EscapeDataString(scalarFilter)}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("InvalidParameterValue");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_NotValueReference_ReturnsServiceException()
    {
        const string notFilter = "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:Not><fes:ValueReference>category</fes:ValueReference></fes:Not></fes:Filter>";
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&FILTER={Uri.EscapeDataString(notFilter)}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("InvalidParameterValue");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_FilterWithSemicolonInLiteral_ReturnsImage()
    {
        // The literal "A;B" contains a semicolon that must not be treated as a layer delimiter
        const string filter = "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsEqualTo><fes:ValueReference>category</fes:ValueReference><fes:Literal>A;B</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>";
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&FILTER={Uri.EscapeDataString(filter)}");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_WithoutFilter_NoRegression()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TRANSPARENT=true");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }
}

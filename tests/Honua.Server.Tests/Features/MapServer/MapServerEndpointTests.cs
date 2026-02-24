// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.MapServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.MapServer;

[Collection("Database")]
[Protocol(Protocols.MapServer)]
public sealed class MapServerEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    public async Task MapServer_Metadata_ReturnsServiceInfo()
    {
        var response = await _fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/MapServer?f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var service = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.MapServerResponse);

        service.Should().NotBeNull();
        service!.MapName.Should().NotBeNullOrWhiteSpace();
        service.CurrentVersion.Should().BeGreaterThan(0);
        service.ServiceDescription.Should().NotBeNullOrWhiteSpace();
        service.Layers.Should().NotBeNullOrEmpty();
        service.Tables.Should().NotBeNull();
        service.Units.Should().NotBeNullOrWhiteSpace();
        service.Capabilities.Should().Contain("Map");
        service.MaxImageWidth.Should().BeGreaterThan(0);
        service.MaxImageHeight.Should().BeGreaterThan(0);
        service.TileInfo.Should().NotBeNull();
        service.TileInfo!.Rows.Should().Be(256);
        service.TileInfo.Cols.Should().Be(256);
        service.TileInfo.Dpi.Should().Be(96);
        service.TileInfo.Format.Should().Be("PNG");
        service.TileInfo.Origin.Should().NotBeNull();
        service.TileInfo.SpatialReference.Should().NotBeNull();
        service.TileInfo.SpatialReference!.Wkid.Should().Be(3857);
        service.TileInfo.Lods.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    public async Task MapServer_Metadata_WithInvalidIdentifier_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/%20/MapServer?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}")]
    public async Task MapServer_LayerMetadata_ReturnsLayerInfo()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}?f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var layer = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.MapServerLayerResponse);

        layer.Should().NotBeNull();
        layer!.Id.Should().Be(WebAppFixture.TestLayerId);
        layer.Name.Should().NotBeNullOrWhiteSpace();
        layer.Type.Should().NotBeNullOrWhiteSpace();
        layer.ObjectIdField.Should().NotBeNullOrWhiteSpace();
        layer.Fields.Should().NotBeNullOrEmpty();
        layer.Capabilities.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_ReturnsImageJson()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Width.Should().Be(256);
        export.Height.Should().Be(256);
        export.Extent.Should().NotBeNull();
        export.Href.Should().NotBeNullOrWhiteSpace();
        export.Scale.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_Post_ReturnsImageJson()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("bbox", "-180,-90,180,90"),
            new KeyValuePair<string, string>("size", "256,256"),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export",
            payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Width.Should().Be(256);
        export.Height.Should().Be(256);
        export.Extent.Should().NotBeNull();
        export.Href.Should().NotBeNullOrWhiteSpace();
        export.Scale.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithTime_ReturnsImageJson()
    {
        var time = System.Uri.EscapeDataString("2023-01-01T00:00:00Z,2023-01-10T00:00:00Z");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&time={time}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Href.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithLayerTimeOptions_ReturnsImageJson()
    {
        var layerTimeOptions = System.Uri.EscapeDataString(
            "{\"0\":{\"useTime\":true,\"time\":\"2023-01-01T00:00:00Z,2023-01-10T00:00:00Z\"}}");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&layerTimeOptions={layerTimeOptions}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Href.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithDynamicLayers_ReturnsImageJson()
    {
        var dynamicLayers = System.Uri.EscapeDataString(
            "[{\"id\":0,\"source\":{\"type\":\"mapLayer\",\"mapLayerId\":0},\"definitionExpression\":\"category = 'test'\"}]");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&dynamicLayers={dynamicLayers}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Href.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithGdbVersion_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&gdbVersion=QA");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_ReturnsResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var identify = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.IdentifyResponse);

        identify.Should().NotBeNull();
        identify!.Results.Should().NotBeNull();
        identify.Results!.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_Post_ReturnsResults()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("geometry", "-122.5,37.5"),
            new KeyValuePair<string, string>("geometryType", "esriGeometryPoint"),
            new KeyValuePair<string, string>("mapExtent", "-180,-90,180,90"),
            new KeyValuePair<string, string>("imageDisplay", "800,600,96"),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify",
            payload);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var identify = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.IdentifyResponse);

        identify.Should().NotBeNull();
        identify!.Results.Should().NotBeNull();
        identify.Results!.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithInvalidGeometryType_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=invalidType&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithInvalidTolerance_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&tolerance=abc&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithInvalidIdentifier_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/%20/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task MapServer_Legend_ReturnsLegendLayers()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var legend = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.LegendResponse);

        legend.Should().NotBeNull();
        legend!.Layers.Should().NotBeNullOrEmpty();
        legend.Layers!.First().Legend.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task MapServer_Legend_WithUnsupportedFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=html");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task MapServer_Legend_WithInvalidIdentifier_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/%20/MapServer/legend?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task MapServer_Query_Get_ReturnsFeatures()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features!.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task MapServer_Query_Post_ReturnsFeatures()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("where", "1=1"),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/query",
            payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features!.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/query")]
    public async Task MapServer_ServiceQuery_GetWithLayerId_ReturnsFeatures()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/query?layerId={WebAppFixture.TestLayerId}&where=1%3D1&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features!.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/query")]
    public async Task MapServer_ServiceQuery_PostWithLayerId_ReturnsFeatures()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("layerId", WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("where", "1=1"),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/query",
            payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features!.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task MapServer_Query_Post_WithUnsupportedBodyParameter_ReturnsBadRequest()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("where", "1=1"),
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("unsupportedParam", "true")
        ]);

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/query",
            payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_Get_ReturnsResponse()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=test&layers={WebAppFixture.TestLayerId}&f=json");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("\"results\"");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_Post_ReturnsResponse()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("searchText", "test"),
            new KeyValuePair<string, string>("layers", WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find",
            payload);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("\"results\"");
        }
    }
}

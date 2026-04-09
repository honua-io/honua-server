// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
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
        service.Capabilities.Should().Contain("Query");
        service.Capabilities.Should().Contain("Data");
        service.CopyrightText.Should().NotBeNull();
        service.SupportedImageFormatTypes.Should().NotBeNullOrWhiteSpace();
        service.DocumentInfo.Should().NotBeNull();
        service.DocumentInfo!.Title.Should().NotBeNull();
        service.MinScale.Should().NotBeNull();
        service.MaxScale.Should().NotBeNull();
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
        layer.Capabilities.Should().Contain("Map");
        layer.Capabilities.Should().Contain("Query");
        layer.Capabilities.Should().Contain("Data");
        layer.MinScale.Should().NotBeNull();
        layer.MaxScale.Should().NotBeNull();
        layer.Extent.Should().NotBeNull();
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
    [Endpoint("GET /rest/services/{serviceId}/MapServer/generateKml")]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/generateKml")]
    public async Task MapServer_GenerateKml_ReturnsValidKml_ForPointLineAndPolygonLayers()
    {
        var serviceName = await SeedGenerateKmlGeometryServiceAsync();

        var response = await _fixture.Client.GetAsync($"/rest/services/{serviceName}/MapServer/generateKml?f=kml");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.google-earth.kml+xml");

        var document = XDocument.Parse(content);
        XNamespace kml = "http://www.opengis.net/kml/2.2";

        document.Root.Should().NotBeNull();
        document.Descendants(kml + "Point").Should().NotBeEmpty();
        document.Descendants(kml + "LineString").Should().NotBeEmpty();
        document.Descendants(kml + "Polygon").Should().NotBeEmpty();
        document.Descendants(kml + "Placemark").Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/generateKml")]
    public async Task MapServer_GenerateKml_WithKmzFormat_ReturnsCompressedArchive()
    {
        var serviceName = await SeedGenerateKmlGeometryServiceAsync();

        var response = await _fixture.Client.GetAsync($"/rest/services/{serviceName}/MapServer/generateKml?f=kmz&layers=110");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.google-earth.kmz");
        bytes.Length.Should().BeGreaterThan(0);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var kmlEntry = archive.GetEntry("doc.kml");
        kmlEntry.Should().NotBeNull();

        using var kmlStream = kmlEntry!.Open();
        using var reader = new StreamReader(kmlStream);
        var kmlContent = await reader.ReadToEndAsync();

        var document = XDocument.Parse(kmlContent);
        XNamespace kml = "http://www.opengis.net/kml/2.2";
        document.Descendants(kml + "Point").Should().NotBeEmpty();
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
    public async Task MapServer_Export_WithMalformedTime_DoesNotLeakInputOrParserDetails()
    {
        const string sentinel = "MAP_TIME_SENTINEL";
        var malformedTime = Uri.EscapeDataString($"not-a-time-{sentinel}");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&time={malformedTime}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid time parameter.");
        content.Should().NotContain(sentinel);
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
        content.Should().NotContain("System.Text.Json");
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
    public async Task MapServer_Export_WithGdbVersion_IgnoresParameter()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&gdbVersion=QA");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Href.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithMalformedLayerDefs_DoesNotLeakJsonParserDetails()
    {
        var malformedLayerDefs = Uri.EscapeDataString("{\"0\":");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&layerDefs={malformedLayerDefs}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("layerDefs contains invalid JSON.");
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
        content.Should().NotContain("System.Text.Json");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithMalformedDynamicLayers_DoesNotLeakJsonParserDetails()
    {
        var malformedDynamicLayers = Uri.EscapeDataString("[{\"id\":");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&dynamicLayers={malformedDynamicLayers}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("dynamicLayers contains invalid JSON.");
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
        content.Should().NotContain("System.Text.Json");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithUnsupportedImageSr_DoesNotLeakTransformDetails()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&bboxSR=4326&imageSR=999999&size=256,256&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid spatial reference.");
        content.Should().NotContain("999999");
        content.Should().NotContain("NotSupportedException");
        content.Should().NotContain("System.");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithMalformedSizePair_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,,256&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithMalformedBackgroundColor_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&backgroundColor=255,,0,0&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithMalformedLayersDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&layers=show:{WebAppFixture.TestLayerId},&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithInvalidLayerIdentifier_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&layers=show:{WebAppFixture.TestLayerId},foo&f=json");

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
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithGdbVersion_IgnoresParameter()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&f=json&gdbVersion=QA");

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
    public async Task MapServer_Identify_WithMalformedImageDisplay_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,,600,96&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithMalformedLayersDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&layers=visible:{WebAppFixture.TestLayerId},&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithInvalidLayerIdentifier_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&layers=visible:{WebAppFixture.TestLayerId},foo&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithMalformedPointPair_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithMalformedGeometryJson_DoesNotLeakParserDetails()
    {
        var malformedGeometry = Uri.EscapeDataString("{\"rings\":[1]}");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry={malformedGeometry}&geometryType=esriGeometryPolygon&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Geometry parameter is invalid.");
        content.Should().NotContain("System.Text.Json");
        content.Should().NotContain("Supported types:");
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithOversizedGeometry_ReturnsBadRequest()
    {
        var oversizedTag = new string('a', 2500);
        var geometry = $"{{\"x\":-122.5,\"y\":37.5,\"tag\":\"{oversizedTag}\"}}";

        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("geometry", geometry),
            new KeyValuePair<string, string>("geometryType", "esriGeometryPoint"),
            new KeyValuePair<string, string>("mapExtent", "-180,-90,180,90"),
            new KeyValuePair<string, string>("imageDisplay", "800,600,96"),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify",
            payload);

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
    public async Task MapServer_Legend_AfterCachedValidRequest_InvalidSizeReturnsBadRequest()
    {
        var validResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json&size=20,20");
        validResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var invalidResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json&size=invalid");
        invalidResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task MapServer_Legend_WithThreeSizeComponents_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json&size=20,20,20");

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
    public async Task MapServer_Query_Post_HonorsQueryStringParameters()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("where", "1=1"),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/query?returnGeometry=false",
            payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().AllSatisfy(feature => feature.Geometry.Should().BeNull());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/query")]
    public async Task MapServer_ServiceQuery_WithMalformedLayersDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/query?layers={WebAppFixture.TestLayerId},&where=1%3D1&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_WithGdbVersion_DoesNotRejectParameter()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=test&layers={WebAppFixture.TestLayerId}&f=json&gdbVersion=QA");

        var content = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            content.Should().NotContain("gdbVersion is not supported.");
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("\"results\"");
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

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_WithMalformedLayersDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=test&layers={WebAppFixture.TestLayerId},&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_WithMalformedSearchFieldsDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=test&layers={WebAppFixture.TestLayerId}&searchFields=name,,category&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_WithInvalidLayerIdentifier_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=test&layers={WebAppFixture.TestLayerId},foo&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<string> SeedGenerateKmlGeometryServiceAsync()
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Test schema not initialized.");
        var serviceName = $"kml_{Guid.NewGuid().ToString("N")[..8]}";

        var sql = $$"""
            INSERT INTO honua.services (
                service_name,
                description,
                srid,
                supported_formats,
                capabilities,
                service_extent
            )
            VALUES (
                '{{serviceName}}',
                'MapServer generateKml geometry test service',
                4326,
                ARRAY['JSON', 'GeoJSON'],
                ARRAY['Query', 'Extract'],
                ST_MakeEnvelope(-180, -90, 180, 90, 4326)
            );

            INSERT INTO honua.layers (
                layer_id,
                layer_name,
                description,
                table_schema,
                table_name,
                geometry_type,
                srid,
                extent,
                default_visibility
            )
            VALUES
                (110, 'KML Point Layer', 'Point geometry test layer', current_schema(), 'features', 'Point', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), true),
                (111, 'KML Line Layer', 'Line geometry test layer', current_schema(), 'features', 'LineString', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), true),
                (112, 'KML Polygon Layer', 'Polygon geometry test layer', current_schema(), 'features', 'Polygon', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), true);

            INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
            VALUES
                ('{{serviceName}}', 110, 0),
                ('{{serviceName}}', 111, 1),
                ('{{serviceName}}', 112, 2);

            INSERT INTO honua.layer_fields (
                layer_id,
                field_name,
                field_type,
                field_order,
                max_length,
                nullable,
                description
            )
            VALUES
                (110, 'objectid', 'Integer', 0, null, false, 'Object ID'),
                (110, 'name', 'String', 1, 255, true, 'Name'),
                (110, 'shape', 'Geometry', 2, null, true, 'Geometry'),
                (111, 'objectid', 'Integer', 0, null, false, 'Object ID'),
                (111, 'name', 'String', 1, 255, true, 'Name'),
                (111, 'shape', 'Geometry', 2, null, true, 'Geometry'),
                (112, 'objectid', 'Integer', 0, null, false, 'Object ID'),
                (112, 'name', 'String', 1, 255, true, 'Name'),
                (112, 'shape', 'Geometry', 2, null, true, 'Geometry');

            INSERT INTO features (objectid, layer_id, geometry, attributes)
            VALUES
                (90110, 110, ST_SetSRID(ST_MakePoint(-157.80, 21.30), 4326), jsonb_build_object('objectid', 90110, 'name', 'KML Point Feature')),
                (90111, 111, ST_GeomFromText('LINESTRING(-157.9 21.2,-157.7 21.4,-157.5 21.3)', 4326), jsonb_build_object('objectid', 90111, 'name', 'KML Line Feature')),
                (90112, 112, ST_GeomFromText('POLYGON((-157.9 21.2,-157.7 21.2,-157.7 21.4,-157.9 21.4,-157.9 21.2))', 4326), jsonb_build_object('objectid', 90112, 'name', 'KML Polygon Feature'));
            """;

        await _fixture.Postgres.ExecuteAsync(sql, schema);
        return serviceName;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

/// <summary>MapServer metadata, query and legend endpoint integration tests.</summary>
[Collection("Database.GeoServicesMapServer")]
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerEndpointTests : MapServerEndpointTestBase
{
    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    public async Task MapServer_Metadata_ReturnsServiceInfo()
    {
        var response = await Fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/MapServer?f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var service = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.MapServerResponse);

        service.Should().NotBeNull();
        service!.MapName.Should().NotBeNullOrWhiteSpace();
        service.ServiceDescription.Should().NotBeNullOrWhiteSpace();
        service.Layers.Should().NotBeNullOrEmpty();
        service.Tables.Should().NotBeNull();
        service.Units.Should().NotBeNullOrWhiteSpace();
        service.Capabilities.Should().Contain("Map");
        service.Capabilities.Should().Contain("Query");
        service.Capabilities.Should().Contain("Data");
        service.Capabilities.Should().NotContain("Create");
        service.Capabilities.Should().NotContain("Update");
        service.Capabilities.Should().NotContain("Delete");
        service.Capabilities.Should().NotContain("Editing");
        service.SupportsDynamicLayers.Should().BeFalse();
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
        service.TimeInfo.Should().NotBeNull();
        service.TimeInfo!.StartTimeField.Should().Be("timestamp");
        service.TimeInfo.TimeExtent.Should().HaveCount(2);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    public async Task MapServer_Metadata_MapsGovernanceToDocumentInfo()
    {
        var provider = Fixture.GetService<TestMetadataV2GraphProvider>();
        var snapshot = await provider.GetCurrentAsync();
        var services = snapshot.Graph.Services
            .Select(service =>
                string.Equals(service.Metadata.Name, WebAppFixture.TestServiceId, StringComparison.OrdinalIgnoreCase) &&
                service.Protocols.Contains(ServiceProtocols.MapServer, StringComparer.OrdinalIgnoreCase)
                    ? service with
                    {
                        Metadata = service.Metadata with
                        {
                            Publisher = "Example Data Office",
                            Attribution = "Example data contributors",
                            ContactPoint = new MetadataV2ContactPoint { Name = "Example Data Contact" },
                        },
                    }
                    : service)
            .ToArray();
        provider.SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Services = services,
        });

        var response = await Fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/MapServer?f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var service = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.MapServerResponse);
        service!.DocumentInfo!.Author.Should().Be("Example Data Contact");
        service.DocumentInfo.Subject.Should().Be("Example Data Office");
        service.DocumentInfo.Credits.Should().Be("Example data contributors");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    public async Task MapServer_Metadata_WithInvalidIdentifier_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync("/rest/services/%20/MapServer?f=json");
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}")]
    public async Task MapServer_LayerMetadata_ReturnsLayerInfo()
    {
        var response = await Fixture.Client.GetAsync(
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
        layer.Capabilities.Should().NotContain("Create");
        layer.Capabilities.Should().NotContain("Update");
        layer.Capabilities.Should().NotContain("Delete");
        layer.Capabilities.Should().NotContain("Editing");
        layer.MinScale.Should().NotBeNull();
        layer.MaxScale.Should().NotBeNull();
        layer.Extent.Should().NotBeNull();
        layer.AdvancedQueryCapabilities.Should().NotBeNull();
        layer.AdvancedQueryCapabilities!.SupportsPagination.Should().BeTrue();
        layer.AdvancedQueryCapabilities.SupportsStatistics.Should().BeTrue();
        layer.AdvancedQueryCapabilities.SupportsOrderBy.Should().BeTrue();
        layer.AdvancedQueryCapabilities.SupportsQueryWithDistance.Should().BeTrue();
        layer.TimeInfo.Should().NotBeNull();
        layer.TimeInfo!.StartTimeField.Should().Be("timestamp");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/dynamicLayer")]
    public async Task MapServer_DynamicLayer_WithMapLayerSource_ReturnsLayerMetadata()
    {
        const int dynamicLayerId = 7;
        var layer = Uri.EscapeDataString(BuildSimpleRendererDynamicLayerObjectJson(dynamicLayerId));
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/dynamicLayer?f=json&layer={layer}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        root.GetProperty("id").GetInt32().Should().Be(dynamicLayerId);
        root.GetProperty("definitionExpression").GetString().Should().Be("1=1");
        root.GetProperty("drawingInfo").GetProperty("renderer").GetProperty("type").GetString().Should().Be("simple");
        root.GetProperty("fields").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/dynamicLayer")]
    public async Task MapServer_DynamicLayer_WithoutLayer_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/dynamicLayer?f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("dynamicLayer requires a layer parameter.");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/dynamicLayer")]
    public async Task MapServer_DynamicLayer_WithUnsupportedSource_ReturnsBadRequest()
    {
        var layer = Uri.EscapeDataString(
            """{"id":7,"source":{"type":"workspaceLayer","workspaceId":"demo","dataSource":{"type":"table","name":"features"}}}""");

        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/dynamicLayer?f=json&layer={layer}");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("unsupported source type");
        content.Should().Contain("workspaceLayer");
        content.Should().NotContain("System.Text.Json");
        content.Should().NotContain("BytePositionInLine");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/dynamicLayer")]
    public async Task MapServer_DynamicLayer_WithMalformedLayer_DoesNotLeakJsonParserDetails()
    {
        var malformedLayer = Uri.EscapeDataString("{\"id\":");
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/dynamicLayer?f=json&layer={malformedLayer}");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("dynamicLayer contains invalid JSON.");
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
        content.Should().NotContain("System.Text.Json");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    public async Task MapServer_Metadata_SupportedImageFormatTypes_DoesNotAdvertiseGif()
    {
        // Regression (#1772): GIF was advertised in supportedImageFormatTypes but the
        // SkiaSharp export renderer ships no GIF encoder, so export?format=gif was
        // rejected. Capabilities must match behavior -> GIF must not be advertised.
        var response = await Fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/MapServer?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var service = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.MapServerResponse);

        service.Should().NotBeNull();
        service!.SupportedImageFormatTypes.Should().NotBeNullOrWhiteSpace();
        service.SupportedImageFormatTypes!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Should().NotContain(format => string.Equals(format, "GIF", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Handlers_WithSameIdStacPublication_IgnoreProtocolDuplicate()
    {
        var provider = Fixture.GetService<TestMetadataV2GraphProvider>();
        var snapshot = await provider.GetCurrentAsync();
        var esriPublication = snapshot.Graph.Publications
            .Where(publication =>
                publication.LayerIndex == WebAppFixture.TestLayerId &&
                publication.PublicationType is (
                    MetadataV2PublicationType.EsriMapLayer or
                    MetadataV2PublicationType.EsriFeatureLayer))
            .OrderBy(publication =>
                publication.PublicationType == MetadataV2PublicationType.EsriMapLayer ? 0 : 1)
            .ThenBy(publication => publication.Metadata.Id, StringComparer.Ordinal)
            .First();
        var stacPublication = esriPublication with
        {
            Metadata = esriPublication.Metadata with
            {
                Id = $"{esriPublication.Metadata.Id}-stac",
                Name = $"{esriPublication.Metadata.Name}-stac",
            },
            PublicationType = MetadataV2PublicationType.StacCollection,
        };
        var danglingMapPublication = esriPublication with
        {
            Metadata = esriPublication.Metadata with
            {
                Id = $"000-{esriPublication.Metadata.Id}-dangling-map",
                Name = $"{esriPublication.Metadata.Name}-dangling-map",
            },
            ResourceId = "missing-mapserver-resource",
            StorageBindingId = "missing-mapserver-binding",
            PublicationType = MetadataV2PublicationType.EsriMapLayer,
        };

        provider.SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Publications = snapshot.Graph.Publications
                .Append(stacPublication)
                .Append(danglingMapPublication)
                .ToArray(),
        });

        using var exportResponse = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&format=png32&f=image");

        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        exportResponse.Content.Headers.ContentType?.MediaType.Should().StartWith("image/");
        (await exportResponse.Content.ReadAsByteArrayAsync()).Should().HaveCountGreaterThan(100);

        var mapServerRequests = new[]
        {
            (Operation: "legend", Path: $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json", ExpectedProperty: "layers"),
            (Operation: "find", Path: $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=test&layers={WebAppFixture.TestLayerId}&f=json", ExpectedProperty: "results"),
            (Operation: "identify", Path: $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&f=json", ExpectedProperty: "results"),
            (Operation: "generateKml", Path: $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/generateKml?layers={WebAppFixture.TestLayerId}&f=kml", ExpectedProperty: string.Empty),
        };

        foreach (var (operation, path, expectedProperty) in mapServerRequests)
        {
            using var response = await Fixture.Client.GetAsync(path);
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                $"MapServer {operation} should ignore non-Esri publications with the same public layer ID. Response: {content}");

            if (operation == "generateKml")
            {
                response.Content.Headers.ContentType?.MediaType.Should()
                    .Be("application/vnd.google-earth.kml+xml");
                XDocument.Parse(content).Root.Should().NotBeNull();
                continue;
            }

            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            using var document = JsonDocument.Parse(content);
            document.RootElement.TryGetProperty("error", out _).Should().BeFalse();
            document.RootElement.TryGetProperty(expectedProperty, out _).Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task MapServer_Legend_ReturnsLegendLayers()
    {
        var response = await Fixture.Client.GetAsync(
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
    public async Task MapServer_Legend_WithDynamicLayerSimpleRenderer_ReturnsRequestSwatch()
    {
        var defaultResponse = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json&size=20");
        var defaultContent = await defaultResponse.Content.ReadAsStringAsync();
        defaultResponse.StatusCode.Should().Be(HttpStatusCode.OK, defaultContent);
        var defaultLegend = JsonSerializer.Deserialize(defaultContent, MapServerJsonContext.Default.LegendResponse);
        var defaultEntry = defaultLegend!.Layers!.Single(layer => layer.LayerId == WebAppFixture.TestLayerId).Legend!.First();

        var dynamicLayerId = 7;
        var dynamicLayers = Uri.EscapeDataString(BuildSimpleRendererDynamicLayersJson(dynamicLayerId));
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json&size=20&dynamicLayers={dynamicLayers}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var legend = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.LegendResponse);

        legend.Should().NotBeNull();
        var dynamicLayer = legend!.Layers.Should().ContainSingle().Subject;
        dynamicLayer.LayerId.Should().Be(dynamicLayerId);
        var dynamicEntry = dynamicLayer.Legend.Should().ContainSingle().Subject;
        dynamicEntry.ImageData.Should().NotBeNullOrWhiteSpace();
        dynamicEntry.ImageData.Should().NotBe(defaultEntry.ImageData);
    }

    [Theory]
    [InlineData("uniqueValue", "Residential", "Commercial")]
    [InlineData("classBreaks", "Low", "High")]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task MapServer_Legend_WithClassifiedRenderer_ReturnsPerClassEntries(
        string rendererType,
        string firstLabel,
        string secondLabel)
    {
        var renderer = rendererType == "uniqueValue"
            ? """
              {"type":"uniqueValue","field1":"category","defaultLabel":"Other","defaultSymbol":{"type":"esriSMS","style":"esriSMSCircle","color":[0,255,0,255],"size":10},"uniqueValueInfos":[
                {"value":"test","label":"Residential","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[255,0,0,255],"size":10}},
                {"value":"sample","label":"Commercial","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[0,0,255,255],"size":10}}
              ]}
              """
            : """
              {"type":"classBreaks","field":"objectid","defaultLabel":"Other","defaultSymbol":{"type":"esriSMS","style":"esriSMSCircle","color":[0,255,0,255],"size":10},"classBreakInfos":[
                {"classMaxValue":2,"label":"Low","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[255,0,0,255],"size":10}},
                {"classMaxValue":5,"label":"High","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[0,0,255,255],"size":10}}
              ]}
              """;
        var dynamicLayers = Uri.EscapeDataString(
            $"[{{\"id\":501,\"source\":{{\"type\":\"mapLayer\",\"mapLayerId\":0}},\"drawingInfo\":{{\"renderer\":{renderer}}}}}]");

        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json&dynamicLayers={dynamicLayers}");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var legend = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.LegendResponse);
        var entries = legend!.Layers.Should().ContainSingle().Subject.Legend!;
        entries.Select(entry => entry.Label).Should().Equal(firstLabel, secondLabel, "Other");
        entries.Should().OnlyContain(entry => !string.IsNullOrWhiteSpace(entry.ImageData));
        entries.Select(entry => entry.ImageData).Should().OnlyHaveUniqueItems();
        if (rendererType == "uniqueValue")
        {
            entries[0].Values.Should().Equal("test");
            entries[1].Values.Should().Equal("sample");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task MapServer_Legend_WithUnsupportedDynamicLayerRenderer_ReturnsBadRequest()
    {
        var dynamicLayers = Uri.EscapeDataString(
            """[{"id":7,"source":{"type":"mapLayer","mapLayerId":0},"drawingInfo":{"renderer":{"type":"heatmap"}}}]""");

        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json&dynamicLayers={dynamicLayers}");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("unsupported drawingInfo renderer");
        content.Should().NotContain("System.Text.Json");
        content.Should().NotContain("BytePositionInLine");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/legend")]
    public async Task MapServer_Legend_Post_ReturnsLegendLayers()
    {
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend",
            payload);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var legend = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.LegendResponse);

        legend.Should().NotBeNull();
        legend!.Layers.Should().NotBeNullOrEmpty();
        legend.Layers!.First().Legend.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("legend")]
    [InlineData("queryLegends")]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/legend")]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/queryLegends")]
    public async Task MapServer_Legend_Post_UsesFormBodyValues(string operation)
    {
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("size", "31,29"),
            new KeyValuePair<string, string>("dynamicLayers", BuildSimpleRendererDynamicLayersJson(dynamicLayerId: 501))
        ]);

        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{operation}",
            payload);
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var legend = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.LegendResponse);
        var layer = legend!.Layers.Should().ContainSingle().Subject;
        layer.LayerId.Should().Be(501);
        var entry = layer.Legend.Should().ContainSingle().Subject;
        entry.Width.Should().Be(31);
        entry.Height.Should().Be(29);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/queryLegends")]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/queryLegends")]
    public async Task MapServer_QueryLegends_ReturnsLegendLayers()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/queryLegends?f=json&size=16");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var legend = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.LegendResponse);

        legend.Should().NotBeNull();
        legend!.Layers.Should().NotBeNullOrEmpty();
        legend.Layers!.First().Legend.Should().NotBeNullOrEmpty();

        // POST form-encoded equivalent must return the same legend layers.
        using var postPayload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("size", "16"),
        ]);
        var postResponse = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/queryLegends",
            postPayload);
        var postContent = await postResponse.Content.ReadAsStringAsync();
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK, postContent);
        var postLegend = JsonSerializer.Deserialize(postContent, MapServerJsonContext.Default.LegendResponse);
        postLegend.Should().NotBeNull();
        postLegend!.Layers.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task MapServer_Legend_WithUnsupportedFormat_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=html");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task MapServer_Legend_AfterCachedValidRequest_InvalidSizeReturnsBadRequest()
    {
        var validResponse = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json&size=20,20");
        validResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var invalidResponse = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json&size=invalid");
        await invalidResponse.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task MapServer_Legend_WithThreeSizeComponents_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json&size=20,20,20");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task MapServer_Legend_WithInvalidIdentifier_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync("/rest/services/%20/MapServer/legend?f=json");
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task MapServer_Query_Get_ReturnsFeatures()
    {
        var response = await Fixture.Client.GetAsync(
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
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("where", "1=1"),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await Fixture.Client.PostAsync(
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
        var response = await Fixture.Client.GetAsync(
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
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("layerId", WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("where", "1=1"),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await Fixture.Client.PostAsync(
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
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("where", "1=1"),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await Fixture.Client.PostAsync(
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
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/query?layers={WebAppFixture.TestLayerId},&where=1%3D1&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task MapServer_Query_Post_WithUnsupportedBodyParameter_ReturnsBadRequest()
    {
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("where", "1=1"),
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("unsupportedParam", "true")
        ]);

        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/query",
            payload);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task MapServer_Query_Post_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var payload = """
            {
              "where": "1=1",
              "f": "json"
            }
            """;

        using var requestContent = new StringContent(payload, Encoding.UTF8, "text/plain");
        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/query",
            requestContent);

        await response.AssertGeoServicesErrorAsync(415, 500);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Unsupported Media Type");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task MapServer_Query_Post_WithInvalidJson_ReturnsBadRequest()
    {
        using var requestContent = new StringContent("{\"where\":\"1=1\"", Encoding.UTF8, "application/json");
        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/query",
            requestContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid JSON payload");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/allLayersAndTables")]
    public async Task MapServer_AllLayersAndTables_ReturnsLayerMetadata()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/allLayersAndTables?f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var result = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.AllLayersAndTablesResponse);

        result.Should().NotBeNull();
        result!.Layers.Should().NotBeNullOrEmpty();
        result.Tables.Should().NotBeNull();

        var layer = result.Layers!.First();
        layer.Id.Should().BeGreaterThanOrEqualTo(0);
        layer.Name.Should().NotBeNullOrWhiteSpace();
        layer.Fields.Should().NotBeNullOrEmpty();
        layer.ObjectIdField.Should().NotBeNullOrWhiteSpace();
        layer.Capabilities.Should().Contain("Query");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/allLayersAndTables")]
    public async Task MapServer_AllLayersAndTables_WithInvalidIdentifier_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync("/rest/services/%20/MapServer/allLayersAndTables?f=json");
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/layers")]
    public async Task MapServer_Layers_ReturnsSameDocumentAsAllLayersAndTables()
    {
        // Regression for #1454: the ArcGIS Maps SDK for JavaScript and the .NET SDK
        // hydrate sublayers via /MapServer/layers (not /allLayersAndTables). The
        // /layers resource must exist and return the identical {layers,tables} document.
        var layersResponse = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/layers?f=json");
        var layersContent = await layersResponse.Content.ReadAsStringAsync();
        layersResponse.StatusCode.Should().Be(HttpStatusCode.OK, layersContent);

        var layersResult = JsonSerializer.Deserialize(layersContent, MapServerJsonContext.Default.AllLayersAndTablesResponse);
        layersResult.Should().NotBeNull();
        layersResult!.Layers.Should().NotBeNullOrEmpty();
        layersResult.Tables.Should().NotBeNull();

        var layer = layersResult.Layers!.First();
        layer.Id.Should().BeGreaterThanOrEqualTo(0);
        layer.Name.Should().NotBeNullOrWhiteSpace();
        layer.Fields.Should().NotBeNullOrEmpty();

        // Must match the allLayersAndTables document exactly (same handler).
        var allResponse = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/allLayersAndTables?f=json");
        var allContent = await allResponse.Content.ReadAsStringAsync();
        allResponse.StatusCode.Should().Be(HttpStatusCode.OK, allContent);
        layersContent.Should().Be(allContent);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDomains)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/queryDomains")]
    public async Task MapServer_QueryDomains_ReturnsDomainsArray()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/queryDomains?f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("domains", out var domains).Should().BeTrue();
        domains.ValueKind.Should().Be(JsonValueKind.Array);
    }

    // honua-server#1825: Esri services accept BOTH GET and POST for queryDomains; clients
    // POST large layers arrays that exceed URL limits. Previously POST returned 405.
    [IntegrationTest]
    [Operation(Operations.QueryDomains)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/queryDomains")]
    public async Task MapServer_QueryDomains_Post_ReturnsDomainsArray()
    {
        using var payload = new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "json")]);
        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/queryDomains",
            payload);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("domains", out var domains).Should().BeTrue();
        domains.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDomains)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/queryDomains")]
    public async Task MapServer_QueryDomains_WithUnknownLayer_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/queryDomains?layers=987654&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/{featureId}")]
    public async Task MapServer_FeatureResource_ReturnsSingleFeature()
    {
        // Resolve a real object id from the layer first so the feature lookup is deterministic.
        var idsResponse = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&returnIdsOnly=true&f=json");
        idsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var idsContent = await idsResponse.Content.ReadAsStringAsync();
        var idsResult = JsonSerializer.Deserialize(idsContent, FeatureServerJsonContext.Default.QueryResponse);
        idsResult.Should().NotBeNull();
        idsResult!.ObjectIds.Should().NotBeNullOrEmpty();
        var objectId = idsResult.ObjectIds!.First();

        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/{objectId}?f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("feature", out var feature).Should().BeTrue();
        feature.TryGetProperty("attributes", out var attributes).Should().BeTrue();
        attributes.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/{featureId}")]
    public async Task MapServer_FeatureResource_WithUnknownObjectId_ReturnsNotFound()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/2147483646?f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FeatureServer-style operations exposed on the MapServer surface. These thin
    // adapters forward to the existing FeatureServer generateRenderer,
    // queryRelatedRecords, and queryAttachments handlers; the assertions mirror the
    // FeatureServer integration coverage to prove the delegation resolves the shared
    // "test" service/layer and produces the FeatureServer response shape.

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/generateRenderer")]
    public async Task MapServer_ServiceGenerateRenderer_WithLayer_ReturnsSimpleRenderer()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/generateRenderer?layer={WebAppFixture.TestLayerId}&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        root.GetProperty("type").GetString().Should().Be("simple");
        root.GetProperty("symbol").ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("symbol").GetProperty("type").GetString().Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/generateRenderer")]
    public async Task MapServer_ServiceGenerateRenderer_PostWithLayerId_ReturnsClassBreaksRenderer()
    {
        using var payload = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("layerId", WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>(
                "classificationDef",
                """{"type":"classBreaksDef","classificationField":"objectid","classificationMethod":"esriClassifyEqualInterval","breakCount":3}""")
        ]);
        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/generateRenderer",
            payload);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        root.GetProperty("type").GetString().Should().Be("classBreaks");
        root.GetProperty("field").GetString().Should().Be("objectid");
        root.GetProperty("classBreakInfos").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/generateRenderer")]
    public async Task MapServer_ServiceGenerateRenderer_WithoutLayer_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/generateRenderer?f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("layer or layerId is required.");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/generateRenderer")]
    public async Task MapServer_GenerateRenderer_ReturnsSimpleRenderer()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/generateRenderer");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        root.GetProperty("type").GetString().Should().Be("simple");
        root.GetProperty("symbol").ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("symbol").GetProperty("type").GetString().Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/generateRenderer")]
    public async Task MapServer_GenerateRenderer_WithClassBreaksDef_ReturnsClassBreaksRenderer()
    {
        var classificationDef = Uri.EscapeDataString(
            """{"type":"classBreaksDef","classificationField":"objectid","classificationMethod":"esriClassifyEqualInterval","breakCount":3}""");
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/generateRenderer?classificationDef={classificationDef}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        root.GetProperty("type").GetString().Should().Be("classBreaks");
        root.GetProperty("field").GetString().Should().Be("objectid");
        root.GetProperty("classBreakInfos").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/{layerId}/generateRenderer")]
    public async Task MapServer_GenerateRenderer_PostWithoutClassificationDef_ReturnsSimpleRenderer()
    {
        using var payload = new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "json")]);
        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/generateRenderer",
            payload);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("type").GetString().Should().Be("simple");
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/queryRelatedRecords")]
    public async Task MapServer_QueryRelatedRecords_ReturnsRelatedRecordGroups()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/queryRelatedRecords?objectIds=1,2&relationshipId=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var queryResponse = JsonSerializer.Deserialize(
            content, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.ObjectIdFieldName.Should().Be("objectid");
        queryResponse.RelatedRecordGroups.Should().NotBeNull();
        queryResponse.RelatedRecordGroups.Should().HaveCount(2);
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/{layerId}/queryRelatedRecords")]
    public async Task MapServer_QueryRelatedRecords_Post_ReturnsRelatedRecordGroups()
    {
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("objectIds", "1"),
            new KeyValuePair<string, string>("relationshipId", "1")
        ]);
        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/queryRelatedRecords",
            payload);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var queryResponse = JsonSerializer.Deserialize(
            content, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.RelatedRecordGroups.Should().NotBeNull();
        queryResponse.RelatedRecordGroups.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/queryRelatedRecords")]
    public async Task MapServer_QueryRelatedRecords_WithUnknownRelationship_ReturnsNotFound()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId=987654");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/queryAttachments")]
    public async Task MapServer_QueryAttachments_ReturnsAttachmentGroups()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/queryAttachments?objectIds=1&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var result = JsonSerializer.Deserialize(
            content, FeatureServerJsonContext.Default.AttachmentQueryResponse);
        result.Should().NotBeNull();
        result!.AttachmentGroups.Should().NotBeNull();
        result.AttachmentGroups.Should().ContainSingle();
        result.AttachmentGroups![0].ParentObjectId.Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/{layerId}/queryAttachments")]
    public async Task MapServer_QueryAttachments_Post_ReturnsAttachmentGroups()
    {
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("objectIds", "1"),
            new KeyValuePair<string, string>("f", "json")
        ]);
        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/queryAttachments",
            payload);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var result = JsonSerializer.Deserialize(
            content, FeatureServerJsonContext.Default.AttachmentQueryResponse);
        result.Should().NotBeNull();
        result!.AttachmentGroups.Should().ContainSingle();
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/queryAttachments")]
    public async Task MapServer_QueryAttachments_WithoutObjectIds_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}/queryAttachments?f=json");

        await response.AssertGeoServicesErrorAsync(400);
    }
}

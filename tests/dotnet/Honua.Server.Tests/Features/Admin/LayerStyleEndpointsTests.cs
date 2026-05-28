// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Styling;
using Honua.Server.Features.Protocols.GeoServices.MapServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for admin layer style endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class LayerStyleEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task GetLayerStyle_ReturnsMapLibreAndDrawingInfo()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.GetAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style");

        response.Be200Ok();

        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            LayerStyleJsonContext.Default.ApiResponseLayerStyleResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.MapLibreStyle.HasValue.Should().BeTrue();
        apiResponse.Data!.DrawingInfo.HasValue.Should().BeTrue();

        apiResponse.Data.MapLibreStyle!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        apiResponse.Data.DrawingInfo!.Value.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/style")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task UnstyledLayer_ReadsAtVersionZero_AndFirstPutLandsAsRevisionOne()
    {
        // Regression for the schema-level styleVersion contract: an unstyled
        // Postgres-backed layer (seed inserts the row without maplibre_style)
        // must read at styleVersion 0 with null revision metadata, so the
        // first operator PUT lands as revision 1, not 2.  Migration 022
        // sets DEFAULT 0 and backfills any rows still at the old DEFAULT 1
        // value; the integration-test seed schemas use DEFAULT 0 directly.
        var client = _fixture.CreateAdminClient();

        var unstyledResponse = await client.GetAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style");
        unstyledResponse.Be200Ok();
        var unstyledPayload = await unstyledResponse.Content.ReadAsStringAsync();
        var unstyled = JsonSerializer.Deserialize(
            unstyledPayload,
            LayerStyleJsonContext.Default.ApiResponseLayerStyleResponse);

        unstyled!.Data!.StyleVersion.Should().Be(0);
        unstyled.Data!.RevisedAt.Should().BeNull();
        unstyled.Data!.RevisedBy.Should().BeNull();
        unstyled.Data!.ChangeSummary.Should().BeNull();

        var layer = new StyleLayerDescriptor(
            WebAppFixture.TestLayerId,
            "Test Layer",
            MetadataV2GeometryType.Point);
        var firstStyle = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        var firstRequest = new LayerStyleUpdateRequest
        {
            MapLibreStyle = JsonSerializer.SerializeToElement(firstStyle),
            ChangedBy = "first-revision-operator",
            ChangeSummary = "Initial revision."
        };

        var firstPutResponse = await client.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            JsonContent.Create(firstRequest, LayerStyleJsonContext.Default.LayerStyleUpdateRequest));
        firstPutResponse.Be200Ok();
        var firstPutPayload = await firstPutResponse.Content.ReadAsStringAsync();
        var firstPut = JsonSerializer.Deserialize(
            firstPutPayload,
            LayerStyleJsonContext.Default.ApiResponseLayerStyleResponse);

        firstPut!.Data!.StyleVersion.Should().Be(1);
        firstPut.Data!.RevisedAt.Should().NotBeNull();
        firstPut.Data!.RevisedBy.Should().Be("first-revision-operator");
        firstPut.Data!.ChangeSummary.Should().Be("Initial revision.");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task UpdateLayerStyle_WithMapLibreStyle_UpdatesAndReturnsStyle()
    {
        var client = _fixture.CreateAdminClient();

        var layer = new StyleLayerDescriptor(
            WebAppFixture.TestLayerId,
            "Test Layer",
            MetadataV2GeometryType.Point);

        var style = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        var updatedName = $"Updated Style {Guid.NewGuid():N}";
        style["name"] = updatedName;
        var mapLibreStyle = JsonSerializer.SerializeToElement(style);

        var request = new LayerStyleUpdateRequest
        {
            MapLibreStyle = mapLibreStyle
        };

        var updateResponse = await client.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            JsonContent.Create(request, LayerStyleJsonContext.Default.LayerStyleUpdateRequest));

        updateResponse.Be200Ok();

        var updatePayload = await updateResponse.Content.ReadAsStringAsync();
        var updateApiResponse = JsonSerializer.Deserialize(
            updatePayload,
            LayerStyleJsonContext.Default.ApiResponseLayerStyleResponse);

        updateApiResponse.Should().NotBeNull();
        updateApiResponse!.Success.Should().BeTrue();
        updateApiResponse.Data.Should().NotBeNull();

        var updatedStyle = updateApiResponse.Data!.MapLibreStyle;
        updatedStyle.HasValue.Should().BeTrue();
        updatedStyle!.Value.GetProperty("name").GetString().Should().Be(updatedName);

        var getResponse = await client.GetAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style");

        getResponse.Be200Ok();

        var getPayload = await getResponse.Content.ReadAsStringAsync();
        var getApiResponse = JsonSerializer.Deserialize(
            getPayload,
            LayerStyleJsonContext.Default.ApiResponseLayerStyleResponse);

        getApiResponse.Should().NotBeNull();
        getApiResponse!.Data.Should().NotBeNull();
        getApiResponse.Data!.MapLibreStyle!.Value.GetProperty("name").GetString().Should().Be(updatedName);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task UpdateLayerStyle_WithInvalidMapLibreStyle_ReturnsBadRequest()
    {
        var client = _fixture.CreateAdminClient();
        var invalidStyle = JsonSerializer.Deserialize<JsonElement>("""
        {
            "version": 8,
            "layers": [
                {
                    "id": "bad-points",
                    "type": "circle",
                    "source": "layer-0",
                    "source-layer": "wrong-layer",
                    "paint": {
                        "circle-color": ["not-an-operator", ["get", "kind"], "#ff0000", "#000000"]
                    }
                }
            ]
        }
        """);
        var request = new LayerStyleUpdateRequest
        {
            MapLibreStyle = invalidStyle
        };

        var response = await client.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            JsonContent.Create(request, LayerStyleJsonContext.Default.LayerStyleUpdateRequest));

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task UpdateLayerStyle_WithInvalidMapLibreColor_ReturnsBadRequest()
    {
        var client = _fixture.CreateAdminClient();
        var layer = new StyleLayerDescriptor(
            WebAppFixture.TestLayerId,
            "Test Layer",
            MetadataV2GeometryType.Point);
        var style = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        var styleLayers = (List<Dictionary<string, object?>>)style["layers"]!;
        var paint = (Dictionary<string, object?>)styleLayers[0]["paint"]!;
        paint["circle-color"] = "not-a-color";

        var request = new LayerStyleUpdateRequest
        {
            MapLibreStyle = JsonSerializer.SerializeToElement(style)
        };

        var response = await client.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            JsonContent.Create(request, LayerStyleJsonContext.Default.LayerStyleUpdateRequest));

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task UpdateLayerStyle_InvalidatesCachedLegendResponses()
    {
        var anonymousClient = _fixture.CreateClient();
        var adminClient = _fixture.CreateAdminClient();

        var legendBefore = await GetFirstLegendImageAsync(anonymousClient);

        var layer = new StyleLayerDescriptor(
            WebAppFixture.TestLayerId,
            "Test Layer",
            MetadataV2GeometryType.Point);
        var style = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        var styleLayers = (List<Dictionary<string, object?>>)style["layers"]!;
        var paint = (Dictionary<string, object?>)styleLayers[0]["paint"]!;
        paint["circle-color"] = "#ff0000";
        paint["circle-radius"] = 12d;

        var request = new LayerStyleUpdateRequest
        {
            MapLibreStyle = JsonSerializer.SerializeToElement(style)
        };

        var updateResponse = await adminClient.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            JsonContent.Create(request, LayerStyleJsonContext.Default.LayerStyleUpdateRequest));

        updateResponse.Be200Ok();

        var legendAfter = await GetFirstLegendImageAsync(anonymousClient);
        legendAfter.Should().NotBe(legendBefore);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task UpdateLayerStyle_InvalidatesCachedTileResponses()
    {
        var anonymousClient = _fixture.CreateClient();
        var adminClient = _fixture.CreateAdminClient();

        var tileBefore = await GetTileImageAsync(anonymousClient);

        var layer = new StyleLayerDescriptor(
            WebAppFixture.TestLayerId,
            "Test Layer",
            MetadataV2GeometryType.Point);
        var style = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        var styleLayers = (List<Dictionary<string, object?>>)style["layers"]!;
        var paint = (Dictionary<string, object?>)styleLayers[0]["paint"]!;
        paint["circle-color"] = "#00ff00";
        paint["circle-radius"] = 14d;

        var request = new LayerStyleUpdateRequest
        {
            MapLibreStyle = JsonSerializer.SerializeToElement(style)
        };

        var updateResponse = await adminClient.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            JsonContent.Create(request, LayerStyleJsonContext.Default.LayerStyleUpdateRequest));

        updateResponse.Be200Ok();

        var tileAfter = await GetTileImageAsync(anonymousClient);
        tileAfter.Should().NotBeEquivalentTo(tileBefore);
    }

    private async Task<string> GetFirstLegendImageAsync(HttpClient client)
    {
        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json");

        response.Be200Ok();

        var payload = await response.Content.ReadAsStringAsync();
        var legend = JsonSerializer.Deserialize(payload, MapServerJsonContext.Default.LegendResponse);

        legend.Should().NotBeNull();
        legend!.Layers.Should().NotBeNullOrEmpty();
        legend.Layers![0].Legend.Should().NotBeNullOrEmpty();
        return legend.Layers[0].Legend![0].ImageData!;
    }

    private async Task<byte[]> GetTileImageAsync(HttpClient client)
    {
        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/0/0/0");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        return await response.Content.ReadAsByteArrayAsync();
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Styling;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for admin layer style endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
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
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task UpdateLayerStyle_WithMapLibreStyle_UpdatesAndReturnsStyle()
    {
        var client = _fixture.CreateAdminClient();

        var layer = LayerDefinition.CreateBasic(
            WebAppFixture.TestLayerId,
            "Test Layer",
            GeometryType.Point);

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
}

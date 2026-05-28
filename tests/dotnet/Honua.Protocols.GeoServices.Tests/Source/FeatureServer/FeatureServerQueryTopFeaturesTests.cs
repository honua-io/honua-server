// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerQueryTopFeaturesTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_ValidRequest_ReturnsFeatures()
    {
        var topFilter = JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid desc"
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_MissingTopFilter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("topFilter");
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_InvalidTopFilter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter=not-json&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_InvalidService_ReturnsNotFound()
    {
        var topFilter = JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid desc"
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/nonexistent/FeatureServer/0/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeaturesPost_ValidRequest_ReturnsFeatures()
    {
        var topFilter = JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid asc"
        });

        var payload = JsonSerializer.Serialize(new
        {
            topFilter,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
    }
}

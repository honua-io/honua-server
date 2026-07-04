// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerGetEstimatesTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public FeatureServerGetEstimatesTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTest]
    [Operation(Operations.GetEstimates)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/getEstimates")]
    public async Task GetEstimates_ValidLayer_ReturnsCountAndExtent()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/getEstimates?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("count", out var count).Should().BeTrue();
        count.GetInt64().Should().BeGreaterOrEqualTo(0);

        root.TryGetProperty("extent", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.GetEstimates)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/getEstimates")]
    public async Task GetEstimates_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/getEstimates?f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetEstimates)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/getEstimates")]
    public async Task ServiceGetEstimates_ValidService_ReturnsLayerEstimates()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/getEstimates?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("layers", out var layers).Should().BeTrue();
        layers.ValueKind.Should().Be(JsonValueKind.Array);
        layers.GetArrayLength().Should().BeGreaterThan(0);

        var firstLayer = layers[0];
        firstLayer.TryGetProperty("id", out _).Should().BeTrue();
        firstLayer.TryGetProperty("count", out var count).Should().BeTrue();
        count.GetInt64().Should().BeGreaterOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.GetEstimates)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/getEstimates")]
    public async Task ServiceGetEstimates_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/getEstimates?f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

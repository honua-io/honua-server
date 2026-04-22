// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.FeatureServer;

[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public sealed class FeatureServerGetEstimatesTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

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

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetEstimates)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/getEstimates")]
    public async Task ServiceGetEstimates_ValidService_ReturnsAggregatedEstimates()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/getEstimates?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("count", out var count).Should().BeTrue();
        count.GetInt64().Should().BeGreaterOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.GetEstimates)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/getEstimates")]
    public async Task ServiceGetEstimates_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/getEstimates?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

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
public sealed class FeatureServerQueryBinsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.QueryBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task QueryBins_ClassificationBin_ReturnsBins()
    {
        var bin = JsonSerializer.Serialize(new
        {
            classificationBin = new { field = "category" }
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBins?bin={Uri.EscapeDataString(bin)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.QueryBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task QueryBins_MissingBin_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBins?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("bin");
    }

    [IntegrationTest]
    [Operation(Operations.QueryBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task QueryBins_InvalidBinJson_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBins?bin=not-json&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.QueryBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task QueryBins_InvalidService_ReturnsNotFound()
    {
        var bin = JsonSerializer.Serialize(new
        {
            classificationBin = new { field = "category" }
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/nonexistent/FeatureServer/0/queryBins?bin={Uri.EscapeDataString(bin)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.QueryBins)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task QueryBinsPost_ValidRequest_ReturnsBins()
    {
        var bin = JsonSerializer.Serialize(new
        {
            classificationBin = new { field = "category" }
        });

        var payload = JsonSerializer.Serialize(new
        {
            bin,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBins",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
    }
}

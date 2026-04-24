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
public sealed class FeatureServerQueryDateBinsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_CalendarBin_ReturnsBins()
    {
        var bin = JsonSerializer.Serialize(new
        {
            calendarBin = new { unit = "month" }
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDateBins?binField=timestamp&bin={Uri.EscapeDataString(bin)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_MissingBinField_ReturnsBadRequest()
    {
        var bin = JsonSerializer.Serialize(new
        {
            calendarBin = new { unit = "month" }
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDateBins?bin={Uri.EscapeDataString(bin)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("binField");
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_MissingBin_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDateBins?binField=timestamp&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("bin");
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_InvalidService_ReturnsNotFound()
    {
        var bin = JsonSerializer.Serialize(new
        {
            calendarBin = new { unit = "month" }
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/nonexistent/FeatureServer/0/queryDateBins?binField=timestamp&bin={Uri.EscapeDataString(bin)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBinsPost_ValidRequest_ReturnsBins()
    {
        var bin = JsonSerializer.Serialize(new
        {
            calendarBin = new { unit = "year" }
        });

        var payload = JsonSerializer.Serialize(new
        {
            binField = "timestamp",
            bin,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDateBins",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
    }
}

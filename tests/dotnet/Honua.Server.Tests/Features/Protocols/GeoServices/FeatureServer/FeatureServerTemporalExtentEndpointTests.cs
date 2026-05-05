// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for the temporal extent endpoint introduced for ticket #379.
/// Exercises the time-aware code path through the standard <see cref="WebAppFixture"/>
/// — the seeded test layer has both a <c>created_at</c> DateTime and an
/// <c>event_date</c> Date column, so the temporal extent helper resolves a range.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerTemporalExtentEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_TimeAwareLayer_ReturnsExtentWithFields()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/temporalExtent?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("layerId").GetInt32().Should().Be(WebAppFixture.TestLayerId);
        root.TryGetProperty("startTimeField", out var startField).Should().BeTrue();
        startField.ValueKind.Should().Be(JsonValueKind.String);
        startField.GetString().Should().NotBeNullOrWhiteSpace();

        // Min/max may be either ISO 8601 or null when no rows exist; but the
        // base seed populates rows so we expect a non-null pair.
        root.TryGetProperty("min", out var min).Should().BeTrue();
        root.TryGetProperty("max", out var max).Should().BeTrue();
        min.ValueKind.Should().Be(JsonValueKind.String);
        max.ValueKind.Should().Be(JsonValueKind.String);

        // Epoch ms variant must mirror the ISO timestamps for ArcGIS-compatible clients.
        root.TryGetProperty("minEpochMs", out var minEpoch).Should().BeTrue();
        root.TryGetProperty("maxEpochMs", out var maxEpoch).Should().BeTrue();
        minEpoch.ValueKind.Should().Be(JsonValueKind.Number);
        maxEpoch.ValueKind.Should().Be(JsonValueKind.Number);
        maxEpoch.GetInt64().Should().BeGreaterThanOrEqualTo(minEpoch.GetInt64());
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_NonexistentLayer_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/9999/temporalExtent?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_NonexistentService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/nonexistent/FeatureServer/{WebAppFixture.TestLayerId}/temporalExtent?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_UnsupportedFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/temporalExtent?f=xml");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_DisallowedQueryParameter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/temporalExtent?where=1=1&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

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
public sealed class FeatureServerQueryH3Tests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_ValidResolution_ReturnsOkOrCapabilityError()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?resolution=7&f=json");

        // The test database may not have h3-pg installed, so accept OK, 501 (missing), or 503 (transient)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotImplemented, HttpStatusCode.ServiceUnavailable);

        var content = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            root.TryGetProperty("features", out var features).Should().BeTrue();
            features.ValueKind.Should().Be(JsonValueKind.Array);
        }
        else
        {
            // Capability error should mention h3-pg
            content.Should().Contain("h3-pg");
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_MissingResolution_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("resolution");
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_InvalidResolution_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?resolution=20&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("resolution");
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/queryH3?resolution=7&f=json");

        // Resolution is valid so validation passes; service lookup fails before h3 capability check
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3Post_ValidRequest_ReturnsOkOrCapabilityError()
    {
        var payload = JsonSerializer.Serialize(new
        {
            resolution = 7,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        // Accept OK (h3-pg available), 501 (missing), or 503 (transient)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotImplemented, HttpStatusCode.ServiceUnavailable);

        var content = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            root.TryGetProperty("features", out var features).Should().BeTrue();
            features.ValueKind.Should().Be(JsonValueKind.Array);
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_NegativeResolution_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?resolution=-1&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_WithKRingDistance_ReturnsOkOrCapabilityError()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?resolution=7&kRingDistance=1&f=json");

        // kRingDistance=1 is valid; outcome depends on h3-pg availability
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotImplemented, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_KRingDistanceZero_ReturnsOkOrCapabilityError()
    {
        // kRingDistance=0 is valid and should take the non-kRing code path
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?resolution=7&kRingDistance=0&f=json");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotImplemented, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_ExcessiveKRingDistance_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?resolution=7&kRingDistance=99&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("kRingDistance");
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3Post_WithOutStatisticsArray_ReturnsOkOrCapabilityError()
    {
        // Verify that a POST body with a native JSON array for outStatistics is accepted
        // (regression test: TryReadRequestValuesAsync flattens JSON arrays into StringValues)
        var payload = JsonSerializer.Serialize(new
        {
            resolution = 7,
            outStatistics = new[]
            {
                new { statisticType = "count", onStatisticField = "objectid", outStatisticFieldName = "cnt" }
            },
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        // Accept OK (h3-pg available), 501 (missing), or 503 (transient) — but NOT 400
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotImplemented, HttpStatusCode.ServiceUnavailable);
    }
}

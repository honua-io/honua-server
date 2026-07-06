// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerServiceQueryTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public FeatureServerServiceQueryTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/query")]
    public async Task ServiceQuery_Get_ReturnsPerLayerResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/query?where=1=1&f=json");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was {0}", content);

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("layers", out var layers).Should().BeTrue();
        layers.ValueKind.Should().Be(JsonValueKind.Array);
        layers.GetArrayLength().Should().BeGreaterThan(0);

        layers.EnumerateArray()
            .Select(layer => layer.GetProperty("id").GetInt32())
            .Should()
            .Contain(WebAppFixture.TestLayerId);

        var firstLayer = layers[0];
        firstLayer.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/query")]
    public async Task ServiceQuery_Get_WithLayerDefs_AppliesPerLayerWhere()
    {
        var layerDefs = Uri.EscapeDataString($"{{\"{WebAppFixture.TestLayerId}\":\"1=0\"}}");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/query?where=1=1&f=json&layerDefs={layerDefs}");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was {0}", content);

        using var document = JsonDocument.Parse(content);
        var targetLayer = document.RootElement
            .GetProperty("layers")
            .EnumerateArray()
            .Single(layer => layer.GetProperty("id").GetInt32() == WebAppFixture.TestLayerId);

        targetLayer.GetProperty("features").GetArrayLength().Should().Be(0);
    }

    // honua-server#1825: Esri services accept BOTH GET and POST for the service-level
    // query op; clients POST large layerDefs/layers arrays. Previously POST returned 405.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/query")]
    public async Task ServiceQuery_Post_ReturnsPerLayerResults()
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("where", "1=1"),
            new KeyValuePair<string, string>("f", "json")
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/query", form);
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was {0}", content);

        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("layers", out var layers).Should().BeTrue();
        layers.ValueKind.Should().Be(JsonValueKind.Array);
        layers.EnumerateArray()
            .Select(layer => layer.GetProperty("id").GetInt32())
            .Should()
            .Contain(WebAppFixture.TestLayerId);
    }

    // honua-server#1825: POST layerDefs (which can exceed URL limits) must apply per-layer.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/query")]
    public async Task ServiceQuery_Post_WithLayerDefs_AppliesPerLayerWhere()
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("where", "1=1"),
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("layerDefs", $"{{\"{WebAppFixture.TestLayerId}\":\"1=0\"}}")
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/query", form);
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was {0}", content);

        using var document = JsonDocument.Parse(content);
        var targetLayer = document.RootElement
            .GetProperty("layers")
            .EnumerateArray()
            .Single(layer => layer.GetProperty("id").GetInt32() == WebAppFixture.TestLayerId);

        targetLayer.GetProperty("features").GetArrayLength().Should().Be(0);
    }

    // honua-server#1825: service-level queryDomains must accept POST (FeatureServer).
    [IntegrationTest]
    [Operation(Operations.QueryDomains)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/queryDomains")]
    public async Task ServiceQueryDomains_Post_ReturnsDomains()
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("f", "json")
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/queryDomains", form);
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was {0}", content);

        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("domains", out var domains).Should().BeTrue();
        domains.ValueKind.Should().Be(JsonValueKind.Array);
    }

    // BH2-011 regression: the service-level query endpoint (/FeatureServer/query, not
    // /FeatureServer/{layerId}/query) always returns a ServiceQueryResponse (JSON). Formats
    // like f=geojson are not supported at the service level. Before the fix, the outer
    // format validation allowed FeatureServerQueryFormats (including geojson), which let the
    // request pass the service-level check and then fail inside the per-layer loop with a
    // confusing HTTP 400. After the fix, the outer guard uses JsonOnlyFormats and the
    // rejection is immediate and correctly attributed.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/query")]
    public async Task ServiceQuery_WithGeoJsonFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/query?where=1=1&f=geojson");

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/query")]
    public async Task ServiceQuery_WithJsonFormat_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/query?where=1=1&f=json");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "f=json must be accepted for service queries; body: {0}", content);
    }
}

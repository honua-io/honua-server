// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerServiceQueryTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/query")]
    public async Task ServiceQuery_Get_ReturnsPerLayerResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/query?where=1=1&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
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

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var targetLayer = document.RootElement
            .GetProperty("layers")
            .EnumerateArray()
            .Single(layer => layer.GetProperty("id").GetInt32() == WebAppFixture.TestLayerId);

        targetLayer.GetProperty("features").GetArrayLength().Should().Be(0);
    }

}

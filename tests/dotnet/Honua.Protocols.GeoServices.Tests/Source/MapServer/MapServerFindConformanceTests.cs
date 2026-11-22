// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

[Protocol(TestProtocols.MapServer)]
public sealed class MapServerFindConformanceTests
{
    private const string ServicePath = $"/rest/services/{MapServerDynamicJoinTests.ServiceName}/MapServer";

    [IntegrationTest]
    [Operation(Operations.Find)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task Find_WithUnknownFieldInLayerDefs_ReturnsBadRequestInsteadOfSkippingLayer()
    {
        using var factory = MapServerDynamicJoinTests.CreateFactory(rightLayerAnonymous: true);
        using var client = factory.CreateClient();

        var layerDefs = Uri.EscapeDataString("""{"0":"nonexistent_column = 'x'"}""");
        using var response = await client.GetAsync(
            $"{ServicePath}/find?searchText=Feature&layers=0&layerDefs={layerDefs}&f=json");
        var content = await response.Content.ReadAsStringAsync();

        await response.AssertGeoServicesErrorAsync(400);
        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("results", out _).Should().BeFalse(content);
    }

    [IntegrationTest]
    [Operation(Operations.Find)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task Find_WithValidLayerDefs_ReturnsMatchingFeatures()
    {
        using var factory = MapServerDynamicJoinTests.CreateFactory(rightLayerAnonymous: true);
        using var client = factory.CreateClient();

        var layerDefs = Uri.EscapeDataString("""{"0":"1=1"}""");
        using var response = await client.GetAsync(
            $"{ServicePath}/find?searchText=Feature&layers=0&layerDefs={layerDefs}&f=json");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("results").GetArrayLength().Should().BeGreaterThan(0, content);
    }
}

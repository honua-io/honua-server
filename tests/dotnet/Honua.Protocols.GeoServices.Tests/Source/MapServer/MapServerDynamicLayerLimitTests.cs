// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

[Collection("Database.GeoServicesMapServer")]
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerDynamicLayerLimitTests : MapServerEndpointTestBase
{
    // Above the default Limits:Query:MaxMapLayersPerRequest (50) and the test service's layer count.
    private const int OverLimitEntries = 120;

    [IntegrationTheory]
    [InlineData("export", "bbox=-180,-90,180,90&size=64,64")]
    [InlineData("legend", "")]
    [InlineData("identify", "geometry=0,0&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=64,64,96&tolerance=1")]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task DynamicLayers_OverTheLayerLimit_AreRefused(string operation, string parameters)
    {
        var document = await GetJsonAsync(operation, parameters, OverLimitEntries);

        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
        document.RootElement.GetProperty("error").GetRawText().Should().Contain("dynamicLayers may contain at most");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task DynamicLayers_WithinTheLimit_AreAccepted()
    {
        var document = await GetJsonAsync("legend", string.Empty, entries: 2);

        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(document.RootElement.GetRawText());
    }

    private async Task<JsonDocument> GetJsonAsync(string operation, string parameters, int entries)
    {
        var dynamicLayers = Uri.EscapeDataString(
            "[" + string.Join(',', Enumerable.Range(1, entries).Select(id => $$$"""{"id":{{{id}}},"source":{"type":"mapLayer","mapLayerId":0}}""")) + "]");
        var query = string.IsNullOrEmpty(parameters) ? string.Empty : parameters + "&";
        using var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{operation}?{query}dynamicLayers={dynamicLayers}&f=json");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}

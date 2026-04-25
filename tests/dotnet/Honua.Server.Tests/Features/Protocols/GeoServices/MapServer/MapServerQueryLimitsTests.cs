// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Server.Features.Protocols.GeoServices.MapServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

[Collection("Database")]
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerQueryLimitsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    private readonly LimitsOptions _limits = new()
    {
        Query = new QueryLimits
        {
            MaxRecordCount = 2
        }
    };

    public async Task InitializeAsync()
    {
        _fixture.ReplaceService<IOptions<LimitsOptions>>(Options.Create(_limits));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    public async Task MapServer_Metadata_WithCustomMaxRecordCount_AdvertisesConfiguredLimit()
    {
        var response = await _fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/MapServer?f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var service = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.MapServerResponse);

        service.Should().NotBeNull();
        service!.MaxRecordCount.Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithConfiguredMaxRecordCount_AppliesGlobalCap()
    {
        var geometry = Uri.EscapeDataString("{\"xmin\":-180,\"ymin\":-90,\"xmax\":180,\"ymax\":90}");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry={geometry}&geometryType=esriGeometryEnvelope&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&layers=all&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var identify = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.IdentifyResponse);

        identify.Should().NotBeNull();
        identify!.Results.Should().HaveCount(2);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_WithConfiguredMaxRecordCount_AppliesGlobalCap()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=Feature&layers=0,1,2&searchFields=name&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var find = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.FindResponse);

        find.Should().NotBeNull();
        find!.Results.Should().HaveCount(2);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

[Protocol(TestProtocols.MapServer)]
[Collection("Database")]
public sealed class MapServerQueryLimitsTests : IClassFixture<MapServerQueryLimitsTests.Fixture>
{
    public sealed class Fixture : IAsyncLifetime
    {
        private static readonly LimitsOptions Limits = new()
        {
            Query = new QueryLimits
            {
                MaxRecordCount = 2
            }
        };

        public WebAppFixture App { get; } =
            new WebAppFixture().ReplaceService<IOptions<LimitsOptions>>(Options.Create(Limits));

        public Task InitializeAsync() => App.InitializeAsync();

        public Task DisposeAsync() => App.DisposeAsync();
    }

    private readonly WebAppFixture _fixture;

    public MapServerQueryLimitsTests(Fixture fixture) => _fixture = fixture.App;

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

    [IntegrationTheory]
    [InlineData("{\"xmin\":-180,\"ymin\":-90,\"xmax\":180,\"ymax\":90}", "-180,-90,180,90", "&sr=4326", 4326)]
    [InlineData("{\"xmin\":-20037508,\"ymin\":-20037508,\"xmax\":20037508,\"ymax\":20037508}", "-20037508,-20037508,20037508,20037508", "", 3857)]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithConfiguredMaxRecordCount_AppliesGlobalCap(
        string geometryJson,
        string mapExtent,
        string spatialReferenceParameter,
        int expectedSrid)
    {
        // Degree inputs declare their CRS; omitted sr uses the advertised Web Mercator CRS.
        var geometry = Uri.EscapeDataString(geometryJson);
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry={geometry}&geometryType=esriGeometryEnvelope{spatialReferenceParameter}&mapExtent={mapExtent}&imageDisplay=800,600,96&layers=all&returnGeometry=true&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var identify = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.IdentifyResponse);

        identify.Should().NotBeNull();
        identify!.Results.Should().HaveCount(2);

        using var document = JsonDocument.Parse(content);
        foreach (var result in document.RootElement.GetProperty("results").EnumerateArray())
        {
            result.GetProperty("geometry").GetProperty("spatialReference").GetProperty("wkid")
                .GetInt32().Should().Be(expectedSrid);
        }
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

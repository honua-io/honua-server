// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VectorTileServer;

[Collection("Database")]
[Protocol(TestProtocols.VectorTileServer)]
public sealed class VectorTileServerStyleZoomTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureServices(services => services.PostConfigure<LimitsOptions>(options =>
        {
            options.Tiles.MinTileZoom = 3;
            options.Tiles.MaxTileZoom = 12;
        }));

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/styles/{**resourcePath}")]
    public async Task RootStyle_ConfiguredTileLimits_NarrowsEverySource()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/resources/styles/root.json");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        var sources = document.RootElement.GetProperty("sources").EnumerateObject().ToArray();
        sources.Should().NotBeEmpty();
        foreach (var source in sources)
        {
            source.Value.GetProperty("minzoom").GetInt32().Should().Be(3);
            source.Value.GetProperty("maxzoom").GetInt32().Should().Be(12);
        }
    }
}

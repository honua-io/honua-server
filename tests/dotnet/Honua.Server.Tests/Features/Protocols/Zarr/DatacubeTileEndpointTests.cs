// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Zarr;

/// <summary>
/// Endpoint-level coverage for the datacube (Zarr coverage) slice -> tile render route (#1835),
/// exercising the public GET so it is backed by a real HTTP request (EndpointRegistryDriftTests).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Tile)]
public sealed class DatacubeTileEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}")]
    public async Task DatacubeTile_LayerWithoutServableCoverage_Returns404()
    {
        // The default test layer has no registered Zarr coverage, so the datacube tile
        // handler resolves no servable coverage and returns 404.
        var response = await _client.GetAsync(
            $"/api/v1/datacubes/{WebAppFixture.TestLayerId}/tiles/WebMercatorQuad/0/0/0");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

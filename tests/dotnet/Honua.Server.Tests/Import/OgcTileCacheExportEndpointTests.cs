// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for the OGC WMTS tile-cache export endpoint (#1016 slice 4).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class OgcTileCacheExportEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-tiles/export")]
    public async Task Export_WithMissingServiceUrl_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/ogc-tiles/export", new
        {
            DryRun = true
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("serviceUrl is required.");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-tiles/export")]
    public async Task Export_NonDryRunWithoutApplyMode_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/ogc-tiles/export", new
        {
            ServiceUrl = "https://wmts.example.test/wmts",
            DryRun = false,
            ApplyMode = false
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("applyMode=true");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-tiles/export")]
    public async Task Export_WithInvalidZoomRange_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/ogc-tiles/export", new
        {
            ServiceUrl = "https://wmts.example.test/wmts",
            DryRun = true,
            MinZoom = 5,
            MaxZoom = 1
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("minZoom");
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the v1 3D Tiles generation admin endpoint (#842).
/// Full end-to-end generation against a PostGIS feature layer is exercised by
/// <see cref="Infrastructure.Scene.SceneTilesPublishExecutorTests"/>; these
/// tests cover the HTTP-surface contract — admin authentication gate and
/// request-payload validation — that the executor never sees.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public class SceneGenerationEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "scene-generation-admin-key";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public SceneGenerationEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/generate")]
    public async Task Generate_Unauthenticated_Returns401()
    {
        using var unauth = _fixture.CreateClient();

        var response = await unauth.PostAsJsonAsync(
            "/api/v1/admin/scenes/generate",
            new GenerateSceneRequest { LayerId = 1 },
            _jsonOptions);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/generate")]
    public async Task Generate_LayerIdMissingOrZero_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/scenes/generate",
            new GenerateSceneRequest { LayerId = 0 },
            _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/generate")]
    public async Task Generate_UnknownLayerId_Returns404()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/scenes/generate",
            new GenerateSceneRequest { LayerId = int.MaxValue },
            _jsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/generate")]
    public async Task Generate_NegativeCacheMaxAgeSeconds_Returns400()
    {
        // The endpoint must forward explicit invalid numeric options to the
        // executor instead of silently dropping them; the executor surfaces
        // SCENE_OPTIONS_INVALID as a 400. Without the forwarding, the request
        // would silently fall back to the server's 3600-second default and
        // surface as 404 (layer-not-found) downstream.
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/scenes/generate",
            new GenerateSceneRequest { LayerId = int.MaxValue, CacheMaxAgeSeconds = -1 },
            _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/generate")]
    public async Task Generate_NonPositiveMaxFeatureCount_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/scenes/generate",
            new GenerateSceneRequest { LayerId = int.MaxValue, MaxFeatureCount = 0 },
            _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

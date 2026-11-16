// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

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

    private readonly string _outputRoot = Path.Join(Path.GetTempPath(), $"scene-output-{Guid.NewGuid():N}");
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
                builder.UseSetting("SceneGeneration:OutputRoot", _outputRoot);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        if (File.Exists(_outputRoot))
        {
            File.Delete(_outputRoot);
        }
        if (Directory.Exists(_outputRoot))
        {
            Directory.Delete(_outputRoot, recursive: true);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/generate")]
    public async Task Generate_UnwritableOutput_Returns500WithSafeOperatorAction()
    {
        await File.WriteAllTextAsync(_outputRoot, "blocks-directory-creation");
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/scenes/generate",
            new GenerateSceneRequest { LayerId = 1, SceneId = $"blocked-{Guid.NewGuid():N}" },
            _jsonOptions);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("SCENE_OUTPUT_UNWRITABLE", body);
        Assert.Contains("SceneGeneration:OutputRoot", body);
        Assert.DoesNotContain(_outputRoot, body);
        Assert.DoesNotContain("retry", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/generate")]
    public async Task Generate_AgentAdministrator_RequiresHumanAndLeavesSceneAbsent()
    {
        var sceneId = $"agent-generation-{Guid.NewGuid():N}";
        var key = await _fixture.Services.GetRequiredService<IAdminApiKeyStore>().CreateAsync(
            "scene-generation-agent", ["admin", "agent"], null, "test", CancellationToken.None);
        using var agent = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", key.Key));

        using var response = await agent.PostAsJsonAsync("/api/v1/admin/scenes/generate",
            new GenerateSceneRequest { LayerId = int.MaxValue, SceneId = sceneId }, _jsonOptions);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("ask one to make this change in Console", await response.Content.ReadAsStringAsync());
        Assert.Null(await _fixture.Services.GetRequiredService<ISceneDatasetRegistry>().FindAsync(sceneId));
    }

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

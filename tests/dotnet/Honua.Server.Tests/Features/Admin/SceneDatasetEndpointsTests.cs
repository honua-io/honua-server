// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the scene dataset registry admin endpoints (#844).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public class SceneDatasetEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "scene-admin-key";
    private const string SharedAssetRoot = "/var/lib/honua/scenes/test";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public SceneDatasetEndpointsTests()
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

    private static string NewSceneId(string suffix) =>
        $"scene-{suffix.ToLowerInvariant()}-{Guid.NewGuid():N}".Substring(0, 32);

    private static RegisterSceneDatasetRequest BuildValidRequest(string id, string? name = null) => new()
    {
        Id = id,
        Name = name ?? $"Scene {id}",
        Description = "Integration test scene",
        AssetRoot = SharedAssetRoot,
        TilesetFileName = "tileset.json",
        DatasetType = "hosted_tiles",
        Extent = new SceneExtentDto { XMin = -10, YMin = -10, XMax = 10, YMax = 10 },
        Crs = "EPSG:4979",
        CachePolicy = new SceneCachePolicyDto { MaxAgeSeconds = 1800, NoStore = false },
        EditionGate = "pro",
        IsPublic = true,
        RequiresAuth = false
    };

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes")]
    public async Task Register_ValidPayload_Returns201WithDetail()
    {
        var id = NewSceneId("a");
        var request = BuildValidRequest(id);

        var response = await _client.PostAsJsonAsync("/api/v1/admin/scenes", request, _jsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<SceneDatasetDetail>(_jsonOptions);

        Assert.NotNull(detail);
        Assert.Equal(id, detail!.Id);
        Assert.Equal(request.Name, detail.Name);
        Assert.Equal(SharedAssetRoot, detail.AssetRoot);
        Assert.Equal(1, detail.Revision);
        Assert.Equal("active", detail.Status);
        Assert.Equal("EPSG:4979", detail.Crs);
        Assert.NotNull(detail.Extent);
        Assert.Equal(1800, detail.CachePolicy.MaxAgeSeconds);
        Assert.NotEqual(Guid.Empty, detail.DatasetId);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes")]
    public async Task Register_DuplicateId_Returns409Problem()
    {
        var id = NewSceneId("dup");
        var first = BuildValidRequest(id, name: $"First {id}");
        var firstResponse = await _client.PostAsJsonAsync("/api/v1/admin/scenes", first, _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        var second = BuildValidRequest(id, name: $"Second {id}");
        var secondResponse = await _client.PostAsJsonAsync("/api/v1/admin/scenes", second, _jsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes")]
    public async Task Register_BadAssetRoot_Returns400Problem()
    {
        var request = BuildValidRequest(NewSceneId("bad-root"));
        request.AssetRoot = "https://cdn.example.com/scene";

        var response = await _client.PostAsJsonAsync("/api/v1/admin/scenes", request, _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes")]
    public async Task Register_BadCrsToken_Returns400Problem()
    {
        var request = BuildValidRequest(NewSceneId("bad-crs"));
        request.Crs = "epsg-4326";

        var response = await _client.PostAsJsonAsync("/api/v1/admin/scenes", request, _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes")]
    public async Task Register_BadTilesetFileName_Returns400Problem()
    {
        var request = BuildValidRequest(NewSceneId("bad-tileset"));
        request.TilesetFileName = "../escape.json";

        var response = await _client.PostAsJsonAsync("/api/v1/admin/scenes", request, _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes")]
    public async Task Register_ConflictingPublicAndAuthFlags_Returns400Problem()
    {
        var request = BuildValidRequest(NewSceneId("conflict"));
        request.IsPublic = true;
        request.RequiresAuth = true;

        var response = await _client.PostAsJsonAsync("/api/v1/admin/scenes", request, _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes")]
    public async Task Register_NeitherPublicNorAuth_Returns400Problem()
    {
        // Both flags false would let the admin record claim no auth while the
        // serving projection treats any non-public record as protected. The
        // validator rejects this contradiction up front.
        var request = BuildValidRequest(NewSceneId("ambig"));
        request.IsPublic = false;
        request.RequiresAuth = false;

        var response = await _client.PostAsJsonAsync("/api/v1/admin/scenes", request, _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes")]
    public async Task Register_PartialExtent_Returns400Problem()
    {
        // Missing xMax and yMax — non-nullable bounds would silently default
        // to 0 and pass range/order validation, so the endpoint must reject
        // partial extent payloads instead of silently accepting them.
        var partialExtent = JsonSerializer.SerializeToNode(new { xMin = -10.0, yMin = -10.0 });
        var request = BuildValidRequest(NewSceneId("partial-extent"));
        var requestNode = JsonSerializer.SerializeToNode(request, _jsonOptions)!.AsObject();
        requestNode["extent"] = partialExtent;

        var response = await _client.PostAsync(
            "/api/v1/admin/scenes",
            new StringContent(requestNode.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/scenes/{id}")]
    public async Task Get_KnownDatasetId_Returns200Detail()
    {
        var detail = await CreateAsync(BuildValidRequest(NewSceneId("get")));

        var response = await _client.GetAsync($"/api/v1/admin/scenes/{detail.DatasetId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fetched = await response.Content.ReadFromJsonAsync<SceneDatasetDetail>(_jsonOptions);
        Assert.NotNull(fetched);
        Assert.Equal(detail.Id, fetched!.Id);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/scenes/{id}")]
    public async Task Get_UnknownDatasetId_Returns404Problem()
    {
        var response = await _client.GetAsync($"/api/v1/admin/scenes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/scenes")]
    public async Task List_ActiveOnly_ByDefault()
    {
        var keep = await CreateAsync(BuildValidRequest(NewSceneId("keep")));
        var drop = await CreateAsync(BuildValidRequest(NewSceneId("drop")));

        var deactivateResponse = await _client.DeleteAsync($"/api/v1/admin/scenes/{drop.DatasetId}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var response = await _client.GetAsync("/api/v1/admin/scenes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summaries = await response.Content.ReadFromJsonAsync<SceneDatasetSummary[]>(_jsonOptions);
        Assert.NotNull(summaries);
        Assert.Contains(summaries!, s => s.DatasetId == keep.DatasetId);
        Assert.DoesNotContain(summaries!, s => s.DatasetId == drop.DatasetId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/scenes")]
    public async Task List_WithIncludeInactive_ReturnsAll()
    {
        var drop = await CreateAsync(BuildValidRequest(NewSceneId("inc")));
        var deactivateResponse = await _client.DeleteAsync($"/api/v1/admin/scenes/{drop.DatasetId}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var response = await _client.GetAsync("/api/v1/admin/scenes?includeInactive=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summaries = await response.Content.ReadFromJsonAsync<SceneDatasetSummary[]>(_jsonOptions);
        Assert.NotNull(summaries);
        Assert.Contains(summaries!, s => s.DatasetId == drop.DatasetId && s.Status == "inactive");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/scenes/{id}")]
    public async Task Update_ChangesFieldsAndBumpsRevision()
    {
        var initial = await CreateAsync(BuildValidRequest(NewSceneId("upd")));

        var update = new UpdateSceneDatasetRequest
        {
            Description = "Updated description",
            CachePolicy = new SceneCachePolicyDto { MaxAgeSeconds = 600, NoStore = true }
        };

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/admin/scenes/{initial.DatasetId}",
            update,
            _jsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<SceneDatasetDetail>(_jsonOptions);
        Assert.NotNull(updated);
        Assert.Equal("Updated description", updated!.Description);
        Assert.Equal(600, updated.CachePolicy.MaxAgeSeconds);
        Assert.True(updated.CachePolicy.NoStore);
        Assert.Equal(initial.Revision + 1, updated.Revision);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/scenes/{id}")]
    public async Task Deactivate_SetsStatusInactive_Returns204()
    {
        var initial = await CreateAsync(BuildValidRequest(NewSceneId("deact")));

        var deactivate = await _client.DeleteAsync($"/api/v1/admin/scenes/{initial.DatasetId}");
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        var get = await _client.GetAsync($"/api/v1/admin/scenes/{initial.DatasetId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var fetched = await get.Content.ReadFromJsonAsync<SceneDatasetDetail>(_jsonOptions);
        Assert.NotNull(fetched);
        Assert.Equal("inactive", fetched!.Status);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/scenes/{id}")]
    public async Task Deactivate_UnknownId_Returns404Problem()
    {
        var response = await _client.DeleteAsync($"/api/v1/admin/scenes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/scenes/{id}/resolve")]
    public async Task Resolve_KnownActiveId_ReturnsSnippetsAndUrl()
    {
        var initial = await CreateAsync(BuildValidRequest(NewSceneId("res")));

        var response = await _client.GetAsync($"/api/v1/admin/scenes/{initial.DatasetId}/resolve");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resolve = await response.Content.ReadFromJsonAsync<SceneDatasetResolveResponse>(_jsonOptions);
        Assert.NotNull(resolve);
        Assert.Equal(initial.Id, resolve!.Id);
        Assert.EndsWith($"/scenes/{initial.Id}/tileset.json", resolve.TilesetUrl, StringComparison.Ordinal);
        Assert.Contains("Cesium3DTileset", resolve.CesiumJsSnippet, StringComparison.Ordinal);
        Assert.Contains("<honua-scene", resolve.HonuaSceneSnippet, StringComparison.Ordinal);
        Assert.Contains(resolve.TilesetUrl, resolve.CesiumJsSnippet, StringComparison.Ordinal);
        Assert.Contains(resolve.TilesetUrl, resolve.HonuaSceneSnippet, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/scenes/{id}/resolve")]
    public async Task Resolve_InactiveId_Returns404Problem()
    {
        var initial = await CreateAsync(BuildValidRequest(NewSceneId("res-inactive")));
        var deactivate = await _client.DeleteAsync($"/api/v1/admin/scenes/{initial.DatasetId}");
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        var response = await _client.GetAsync($"/api/v1/admin/scenes/{initial.DatasetId}/resolve");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/scenes")]
    public async Task AllRoutes_Unauthenticated_Returns401()
    {
        using var unauth = _fixture.CreateClient();

        var listResponse = await unauth.GetAsync("/api/v1/admin/scenes");
        Assert.Equal(HttpStatusCode.Unauthorized, listResponse.StatusCode);

        var registerResponse = await unauth.PostAsJsonAsync(
            "/api/v1/admin/scenes",
            BuildValidRequest(NewSceneId("anon")),
            _jsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, registerResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes")]
    public async Task FindAsync_ActiveScene_ReturnsServingModel()
    {
        var initial = await CreateAsync(BuildValidRequest(NewSceneId("serve")));

        using var scope = _fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISceneDatasetRegistry>();
        var resolved = await registry.FindAsync(initial.Id);

        Assert.NotNull(resolved);
        Assert.Equal(initial.Id, resolved!.Id);
        Assert.Equal(initial.AssetRoot, resolved.AssetRoot);
        Assert.Equal("tileset.json", resolved.TilesetFileName);
        Assert.Null(resolved.Metadata); // public scene → no access policy
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes")]
    [Endpoint("DELETE /api/v1/admin/scenes/{id}")]
    public async Task FindAsync_DeactivatedScene_ReturnsNull()
    {
        var initial = await CreateAsync(BuildValidRequest(NewSceneId("hidden")));
        var deactivate = await _client.DeleteAsync($"/api/v1/admin/scenes/{initial.DatasetId}");
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        using var scope = _fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISceneDatasetRegistry>();
        var resolved = await registry.FindAsync(initial.Id);

        Assert.Null(resolved);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes")]
    public async Task FindAsync_ProtectedScene_PropagatesAccessPolicy()
    {
        var request = BuildValidRequest(NewSceneId("priv"));
        request.IsPublic = false;
        request.RequiresAuth = true;
        request.AllowedRoles = ["admin", "editor"];

        var detail = await CreateAsync(request);

        using var scope = _fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISceneDatasetRegistry>();
        var resolved = await registry.FindAsync(detail.Id);

        Assert.NotNull(resolved);
        Assert.NotNull(resolved!.Metadata);
        Assert.NotNull(resolved.Metadata!.AccessPolicy);
        Assert.False(resolved.Metadata.AccessPolicy!.AllowAnonymous);
        Assert.Contains("admin", resolved.Metadata.AccessPolicy.AllowedRoles!);
    }

    private async Task<SceneDatasetDetail> CreateAsync(RegisterSceneDatasetRequest request)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/scenes", request, _jsonOptions);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"POST /api/v1/admin/scenes returned {(int)response.StatusCode}: {body}");
        }

        var detail = await response.Content.ReadFromJsonAsync<SceneDatasetDetail>(_jsonOptions);
        Assert.NotNull(detail);
        return detail!;
    }
}

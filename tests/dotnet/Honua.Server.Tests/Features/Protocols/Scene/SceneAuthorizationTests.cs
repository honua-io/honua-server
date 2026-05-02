// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Authorization tests covering protected scenes. Public scenes (no access
/// policy) are exercised in <see cref="SceneTilesetEndpointTests"/>; this
/// fixture disables dev-auth so authorization gaps fail loudly.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class SceneAuthorizationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;

    public SceneAuthorizationTests()
    {
        var fixtureRoot = SceneFixturePaths.ResolveFixtureRoot();

        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        // Public scene — no access policy.
                        [$"Scenes:Datasets:0:Id"] = SceneFixturePaths.FixtureSceneId,
                        [$"Scenes:Datasets:0:Name"] = "Public Fixture",
                        [$"Scenes:Datasets:0:AssetRoot"] = fixtureRoot,

                        // Protected scene — requires authenticated principal,
                        // no anonymous access.
                        [$"Scenes:Datasets:1:Id"] = SceneFixturePaths.ProtectedSceneId,
                        [$"Scenes:Datasets:1:Name"] = "Protected Fixture",
                        [$"Scenes:Datasets:1:AssetRoot"] = fixtureRoot,
                        [$"Scenes:Datasets:1:AccessPolicy:AllowAnonymous"] = "false"
                    });
                });
            });
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_PublicScene_DoesNotRequireAuth()
    {
        var response = await _fixture.Client.GetAsync($"/scenes/{SceneFixturePaths.FixtureSceneId}/tileset.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_ProtectedScene_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _fixture.Client.GetAsync($"/scenes/{SceneFixturePaths.ProtectedSceneId}/tileset.json");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_ProtectedScene_NestedAsset_WithoutAuth_ReturnsUnauthorized()
    {
        // Acceptance criterion: the access policy applies to nested asset
        // requests, not just the root tileset.json.
        var response = await _fixture.Client.GetAsync($"/scenes/{SceneFixturePaths.ProtectedSceneId}/tiles/0.b3dm");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_ProtectedScene_NestedTileset_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _fixture.Client.GetAsync($"/scenes/{SceneFixturePaths.ProtectedSceneId}/nested/sub-tileset.json");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

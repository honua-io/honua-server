// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Scene;

// Scene discovery describes a scene only to a caller who may fetch its assets.
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class SceneDiscoveryVisibilityTests : IAsyncLifetime
{
    private const string AdminPassword = "scene-discovery-test-key";
    private readonly WebAppFixture _fixture;
    private HttpClient _authenticatedClient = null!;

    public SceneDiscoveryVisibilityTests()
    {
        var fixtureRoot = SceneFixturePaths.ResolveFixtureRoot();

        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scenes:Datasets:0:Id"] = SceneFixturePaths.FixtureSceneId,
                        ["Scenes:Datasets:0:Name"] = "Public Fixture",
                        ["Scenes:Datasets:0:AssetRoot"] = fixtureRoot,
                        ["Scenes:Datasets:1:Id"] = SceneFixturePaths.ProtectedSceneId,
                        ["Scenes:Datasets:1:Name"] = "Protected Fixture",
                        ["Scenes:Datasets:1:AssetRoot"] = fixtureRoot,
                        ["Scenes:Datasets:1:AccessPolicy:AllowAnonymous"] = "false"
                    });
                });
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _authenticatedClient = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/scenes")]
    public async Task ListScenes_WithoutAuth_OmitsProtectedScene()
    {
        var sceneIds = await ListSceneIdsAsync(_fixture.Client, "/api/scenes");

        sceneIds.Should().Contain(SceneFixturePaths.FixtureSceneId);
        sceneIds.Should().NotContain(SceneFixturePaths.ProtectedSceneId);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/scenes")]
    public async Task ListScenes_WithAuth_ListsProtectedSceneWithoutRoleNames()
    {
        using var response = await _authenticatedClient.GetAsync("/api/scenes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var scene = document.RootElement.GetProperty("scenes").EnumerateArray()
            .Should().ContainSingle(item => item.GetProperty("id").GetString() == SceneFixturePaths.ProtectedSceneId).Subject;
        var auth = scene.GetProperty("auth");

        auth.GetProperty("requiresAuthentication").GetBoolean().Should().BeTrue();
        auth.TryGetProperty("policy", out _).Should().BeFalse();
    }

    [IntegrationTheory]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/scenes/{sceneId}")]
    [Endpoint("GET /api/scenes/{sceneId}/resolve")]
    [InlineData("")]
    [InlineData("/resolve")]
    public async Task DescribeScene_ProtectedScene_FollowsAssetAccess(string suffix)
    {
        var path = $"/api/scenes/{SceneFixturePaths.ProtectedSceneId}{suffix}";

        using var anonymous = await _fixture.Client.GetAsync(path);
        using var authenticated = await _authenticatedClient.GetAsync(path);

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        authenticated.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTheory]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/scenes/{sceneId}")]
    [Endpoint("GET /api/scenes/{sceneId}/resolve")]
    [InlineData("")]
    [InlineData("/resolve")]
    public async Task DescribeScene_PublicScene_DoesNotRequireAuth(string suffix)
    {
        using var response = await _fixture.Client.GetAsync($"/api/scenes/{SceneFixturePaths.FixtureSceneId}{suffix}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/scenes")]
    public async Task ListScenes_IncludeDisabled_RequiresAdministrator()
    {
        using var anonymous = await _fixture.Client.GetAsync("/api/scenes?includeDisabled=true");
        var administratorSceneIds = await ListSceneIdsAsync(_authenticatedClient, "/api/scenes?includeDisabled=true");

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        administratorSceneIds.Should().Contain(new[] { SceneFixturePaths.FixtureSceneId, SceneFixturePaths.ProtectedSceneId });
    }

    private static async Task<string?[]> ListSceneIdsAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("scenes").EnumerateArray()
            .Select(static scene => scene.GetProperty("id").GetString())
            .ToArray();
    }
}

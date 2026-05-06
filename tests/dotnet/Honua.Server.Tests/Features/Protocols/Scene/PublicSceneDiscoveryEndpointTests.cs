// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Public SDK-compatible scene discovery coverage for the shipped
/// downtown-honolulu fixture used by mobile live-image tests.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class PublicSceneDiscoveryEndpointTests : IAsyncLifetime
{
    private const string SceneId = "downtown-honolulu";
    private readonly WebAppFixture _fixture;

    public PublicSceneDiscoveryEndpointTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder => builder.UseSetting("HONUA_DEV_AUTH", "false"));
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/scenes")]
    public async Task ListScenes_With3dTilesCapability_ReturnsDowntownHonoluluFixture()
    {
        var response = await _fixture.Client.GetAsync("/api/scenes?f=json&capabilities=3d-tiles");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var scenes = document.RootElement.GetProperty("scenes").EnumerateArray().ToArray();
        var scene = scenes.Should().ContainSingle(item => item.GetProperty("id").GetString() == SceneId).Subject;

        scene.GetProperty("name").GetString().Should().Be("Downtown Honolulu");
        scene.GetProperty("tilesetUrl").GetString().Should().EndWith($"/scenes/{SceneId}/tileset.json");
        scene.GetProperty("capabilities").EnumerateArray()
            .Select(item => item.GetString())
            .Should().Contain("3d-tiles");
        scene.GetProperty("auth").GetProperty("requiresAuthentication").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/scenes/{sceneId}")]
    public async Task GetScene_ForDowntownHonolulu_ReturnsSdkMetadata()
    {
        var response = await _fixture.Client.GetAsync($"/api/scenes/{SceneId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("id").GetString().Should().Be(SceneId);
        root.GetProperty("tileset").GetProperty("url").GetString()
            .Should().EndWith($"/scenes/{SceneId}/tileset.json");
        root.GetProperty("tileset").GetProperty("format").GetString().Should().Be("3d-tiles");
        root.GetProperty("links").EnumerateArray()
            .Select(link => link.GetProperty("rel").GetString())
            .Should().Contain("resolve");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/scenes/{sceneId}/resolve")]
    public async Task ResolveScene_ForDowntownHonolulu_ReturnsHostedTilesetUrl()
    {
        var response = await _fixture.Client.GetAsync(
            $"/api/scenes/{SceneId}/resolve?f=json&capabilities=3d-tiles&includeTerrain=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("sceneId").GetString().Should().Be(SceneId);
        root.GetProperty("tilesetUrl").GetString().Should().EndWith($"/scenes/{SceneId}/tileset.json");
        root.GetProperty("endpoints").EnumerateArray().Should().ContainSingle(endpoint =>
            endpoint.GetProperty("kind").GetString() == "3d-tiles" &&
            endpoint.GetProperty("url").GetString()!.EndsWith($"/scenes/{SceneId}/tileset.json", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_ForDowntownHonoluluFixture_ReturnsHostedTilesetJson()
    {
        var response = await _fixture.Client.GetAsync($"/scenes/{SceneId}/tileset.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("asset").GetProperty("version").GetString().Should().Be("1.1");
        document.RootElement.GetProperty("extras").GetProperty("sceneId").GetString().Should().Be(SceneId);
        document.RootElement.GetProperty("root").GetProperty("content").GetProperty("uri").GetString()
            .Should().Be("tiles/0.b3dm");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_ForDowntownHonoluluFixture_ReturnsTileBinary()
    {
        var response = await _fixture.Client.GetAsync($"/scenes/{SceneId}/tiles/0.b3dm");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/octet-stream");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(4);
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("b3dm");
    }
}

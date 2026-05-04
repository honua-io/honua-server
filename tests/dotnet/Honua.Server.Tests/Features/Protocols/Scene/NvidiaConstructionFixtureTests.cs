// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// HTTP integration coverage for the NVIDIA construction demo fixture
/// (<c>tests/fixtures/scenes/nvidia-construction/</c>): both scene ids serve
/// their tilesets, the observations sidecar is reachable via the static scene
/// asset path, and shared-AssetRoot routing keeps each id on its own tileset.
/// <para>
/// The fixture is registered via in-memory configuration through
/// <see cref="WebAppFixture"/>, which provisions a test-managed Postgres
/// container so the configuration registry composes behind the same Postgres
/// composite that production runs. Pure on-disk JSON / b3dm assertions live
/// in <see cref="NvidiaConstructionFixtureFileTests"/> on the fast tier.
/// </para>
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class NvidiaConstructionFixtureTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;

    public NvidiaConstructionFixtureTests()
    {
        var fixtureRoot = NvidiaConstructionFixturePaths.ResolveFixtureRoot();

        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"Scenes:Datasets:0:Id"] = NvidiaConstructionFixturePaths.MainSceneId,
                        [$"Scenes:Datasets:0:Name"] = "NVIDIA Demo – Santa Clara Construction Site",
                        [$"Scenes:Datasets:0:AssetRoot"] = fixtureRoot,
                        [$"Scenes:Datasets:0:TilesetFileName"] = NvidiaConstructionFixturePaths.MainTilesetFileName,
                        [$"Scenes:Datasets:1:Id"] = NvidiaConstructionFixturePaths.ObsSceneId,
                        [$"Scenes:Datasets:1:Name"] = "NVIDIA Demo – Site Observations",
                        [$"Scenes:Datasets:1:AssetRoot"] = fixtureRoot,
                        [$"Scenes:Datasets:1:TilesetFileName"] = NvidiaConstructionFixturePaths.ObsTilesetFileName
                    });
                });
            });
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_MainScene_ReturnsExtrasWithCameraAndProjectMetadata()
    {
        var response = await _fixture.Client.GetAsync(
            $"/scenes/{NvidiaConstructionFixturePaths.MainSceneId}/tileset.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        response.Headers.ETag.Should().NotBeNull();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var extras = doc.RootElement.GetProperty("extras");

        extras.GetProperty("layerKind").GetString().Should().Be("structure");
        extras.GetProperty("cameraHint").GetProperty("longitude").GetDouble()
            .Should().BeInRange(-122.0, -121.5);
        extras.GetProperty("projectMeta").GetProperty("id").GetString()
            .Should().Be("nvidia-construction-demo-2026");
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_ObsScene_ReturnsObservationsLayerExtras()
    {
        var response = await _fixture.Client.GetAsync(
            $"/scenes/{NvidiaConstructionFixturePaths.ObsSceneId}/tileset.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("extras").GetProperty("layerKind").GetString()
            .Should().Be("observations");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_StructureTile_ReturnsB3dmBinary()
    {
        var response = await _fixture.Client.GetAsync(
            $"/scenes/{NvidiaConstructionFixturePaths.MainSceneId}/{NvidiaConstructionFixturePaths.StructureTileRelativePath}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/octet-stream");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(4);
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("b3dm");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_ObservationsSidecar_ReturnsJsonWithStableSceneId()
    {
        // The sidecar is served through the same scene asset resolver as
        // tile binaries, so the demo client can fetch it with one base URL.
        var response = await _fixture.Client.GetAsync(
            $"/scenes/{NvidiaConstructionFixturePaths.MainSceneId}/{NvidiaConstructionFixturePaths.ObservationsSidecarFileName}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("sceneId").GetString()
            .Should().Be(NvidiaConstructionFixturePaths.MainSceneId);
        doc.RootElement.GetProperty("observations").GetArrayLength().Should().Be(5);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_ObsScene_RegisteredViaSharedAssetRoot_ReturnsObsTilesetNotMain()
    {
        // Both scene IDs share AssetRoot but differ on TilesetFileName.
        // Verify the registry routes each ID to its own tileset document.
        var main = await _fixture.Client.GetAsync(
            $"/scenes/{NvidiaConstructionFixturePaths.MainSceneId}/tileset.json");
        var obs = await _fixture.Client.GetAsync(
            $"/scenes/{NvidiaConstructionFixturePaths.ObsSceneId}/tileset.json");

        main.StatusCode.Should().Be(HttpStatusCode.OK);
        obs.StatusCode.Should().Be(HttpStatusCode.OK);

        using var mainDoc = JsonDocument.Parse(await main.Content.ReadAsStringAsync());
        using var obsDoc = JsonDocument.Parse(await obs.Content.ReadAsStringAsync());

        mainDoc.RootElement.GetProperty("extras").GetProperty("layerKind").GetString().Should().Be("structure");
        obsDoc.RootElement.GetProperty("extras").GetProperty("layerKind").GetString().Should().Be("observations");
    }
}

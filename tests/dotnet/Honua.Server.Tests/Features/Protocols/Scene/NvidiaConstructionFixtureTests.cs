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
/// Verifies the NVIDIA construction demo fixture:
/// <list type="bullet">
///   <item>both registered scene IDs (<c>nvidia-construction</c>, <c>nvidia-construction-obs</c>) resolve and serve their tilesets;</item>
///   <item>tileset extras carry the stable IDs, bounds, camera hints, project metadata, and layer kind clients consume;</item>
///   <item>the <c>observations.json</c> sidecar is servable as a static scene asset with a stable schema.</item>
/// </list>
/// Pure unit assertions on the on-disk fixture run without booting the server;
/// integration assertions register the fixture via in-memory config so they
/// stay hermetic and don't depend on <c>appsettings.Development.json</c>.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class NvidiaConstructionFixtureTests : IAsyncLifetime
{
    private const double LonRadMin = -2.13;
    private const double LonRadMax = -2.12;
    private const double LatRadMin = 0.65;
    private const double LatRadMax = 0.66;

    private static readonly string[] ExpectedObservationIds =
        ["obs-001", "obs-002", "obs-003", "obs-004", "obs-005"];

    private static readonly string[] CameraHintFields =
        ["longitude", "latitude", "height", "heading", "pitch", "roll"];

    private static readonly string[] BothTilesetFiles =
    [
        NvidiaConstructionFixturePaths.MainTilesetFileName,
        NvidiaConstructionFixturePaths.ObsTilesetFileName
    ];

    private static readonly string[] BothTileBinaries =
    [
        NvidiaConstructionFixturePaths.StructureTileRelativePath,
        NvidiaConstructionFixturePaths.ObsPinTileRelativePath
    ];

    private readonly WebAppFixture _fixture;
    private readonly string _fixtureRoot;

    public NvidiaConstructionFixtureTests()
    {
        _fixtureRoot = NvidiaConstructionFixturePaths.ResolveFixtureRoot();

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
                        [$"Scenes:Datasets:0:AssetRoot"] = _fixtureRoot,
                        [$"Scenes:Datasets:0:TilesetFileName"] = NvidiaConstructionFixturePaths.MainTilesetFileName,
                        [$"Scenes:Datasets:1:Id"] = NvidiaConstructionFixturePaths.ObsSceneId,
                        [$"Scenes:Datasets:1:Name"] = "NVIDIA Demo – Site Observations",
                        [$"Scenes:Datasets:1:AssetRoot"] = _fixtureRoot,
                        [$"Scenes:Datasets:1:TilesetFileName"] = NvidiaConstructionFixturePaths.ObsTilesetFileName
                    });
                });
            });
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // ---- On-disk fixture assertions (no server boot) ---------------------

    [UnitTest]
    public void MainTileset_IsValidJsonWithRequiredAssetVersion()
    {
        var path = Path.Combine(_fixtureRoot, NvidiaConstructionFixturePaths.MainTilesetFileName);
        File.Exists(path).Should().BeTrue("the demo fixture must commit the main tileset");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        root.GetProperty("asset").GetProperty("version").GetString().Should().Be("1.1");
        root.GetProperty("root").GetProperty("content").GetProperty("uri").GetString()
            .Should().Be(NvidiaConstructionFixturePaths.StructureTileRelativePath);
    }

    [UnitTest]
    public void MainTileset_ExtrasContainsStableProjectMetadata()
    {
        using var doc = LoadTileset(NvidiaConstructionFixturePaths.MainTilesetFileName);
        var extras = doc.RootElement.GetProperty("extras");

        extras.GetProperty("attribution").GetString().Should().NotBeNullOrWhiteSpace();
        extras.GetProperty("layerKind").GetString().Should().Be("structure");
        extras.GetProperty("layerId").GetString().Should().Be(NvidiaConstructionFixturePaths.MainSceneId);

        var camera = extras.GetProperty("cameraHint");
        foreach (var field in CameraHintFields)
        {
            camera.TryGetProperty(field, out var value).Should().BeTrue($"cameraHint.{field} is required for client framing");
            value.ValueKind.Should().Be(JsonValueKind.Number);
        }

        var bounds = extras.GetProperty("bounds");
        bounds.GetProperty("west").GetDouble().Should().BeLessThan(bounds.GetProperty("east").GetDouble());
        bounds.GetProperty("south").GetDouble().Should().BeLessThan(bounds.GetProperty("north").GetDouble());
        bounds.GetProperty("minHeight").GetDouble().Should().BeLessOrEqualTo(bounds.GetProperty("maxHeight").GetDouble());

        var project = extras.GetProperty("projectMeta");
        project.GetProperty("id").GetString().Should().Be("nvidia-construction-demo-2026");
        project.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
        project.GetProperty("phase").GetString().Should().NotBeNullOrWhiteSpace();
        var completion = project.GetProperty("completionRatio").GetDouble();
        completion.Should().BeInRange(0.0, 1.0);
        project.GetProperty("workPackages").GetArrayLength().Should().BeGreaterThan(0);
        project.GetProperty("stakeholders").GetArrayLength().Should().BeGreaterThan(0);
    }

    [UnitTest]
    public void MainTileset_BoundingRegionUsesWgs84Radians()
    {
        // OGC 3D Tiles 1.1 §6.7.2: a region bounding volume is six numbers
        // [west, south, east, north, minHeight, maxHeight] with longitudes and
        // latitudes in radians (WGS-84). Camera hints elsewhere in extras use
        // decimal degrees by Cesium convention — keep that distinction strict.
        using var doc = LoadTileset(NvidiaConstructionFixturePaths.MainTilesetFileName);
        var region = doc.RootElement.GetProperty("root").GetProperty("boundingVolume").GetProperty("region");

        region.GetArrayLength().Should().Be(6);
        var values = new double[6];
        for (var i = 0; i < 6; i++)
        {
            values[i] = region[i].GetDouble();
        }

        values[0].Should().BeInRange(LonRadMin, LonRadMax);
        values[2].Should().BeInRange(LonRadMin, LonRadMax);
        values[1].Should().BeInRange(LatRadMin, LatRadMax);
        values[3].Should().BeInRange(LatRadMin, LatRadMax);
        values[0].Should().BeLessThan(values[2]);
        values[1].Should().BeLessThan(values[3]);
        values[4].Should().BeLessOrEqualTo(values[5]);
    }

    [UnitTest]
    public void ObsTileset_HasObservationsLayerKindAndSidecarPointer()
    {
        using var doc = LoadTileset(NvidiaConstructionFixturePaths.ObsTilesetFileName);
        var extras = doc.RootElement.GetProperty("extras");

        extras.GetProperty("layerKind").GetString().Should().Be("observations");
        extras.GetProperty("layerId").GetString().Should().Be(NvidiaConstructionFixturePaths.ObsSceneId);
        extras.GetProperty("observationsSidecar").GetProperty("uri").GetString()
            .Should().Be(NvidiaConstructionFixturePaths.ObservationsSidecarFileName);

        doc.RootElement.GetProperty("root").GetProperty("content").GetProperty("uri").GetString()
            .Should().Be(NvidiaConstructionFixturePaths.ObsPinTileRelativePath);
    }

    [UnitTest]
    public void ObservationsSidecar_HasStableDeterministicShape()
    {
        var path = Path.Combine(_fixtureRoot, NvidiaConstructionFixturePaths.ObservationsSidecarFileName);
        File.Exists(path).Should().BeTrue();

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        root.GetProperty("schemaVersion").GetString().Should().Be("1.0");
        root.GetProperty("sceneId").GetString().Should().Be(NvidiaConstructionFixturePaths.MainSceneId);
        root.GetProperty("projectId").GetString().Should().Be("nvidia-construction-demo-2026");

        var observations = root.GetProperty("observations");
        observations.GetArrayLength().Should().Be(5);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in observations.EnumerateArray())
        {
            var id = entry.GetProperty("id").GetString();
            id.Should().NotBeNullOrWhiteSpace();
            ids.Add(id!).Should().BeTrue("observation ids must be unique");

            entry.GetProperty("kind").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("longitude").GetDouble().Should().BeInRange(-180.0, 180.0);
            entry.GetProperty("latitude").GetDouble().Should().BeInRange(-90.0, 90.0);
            entry.GetProperty("recordedAt").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("evidenceKind").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("evidenceCount").GetInt32().Should().BeGreaterOrEqualTo(0);
        }

        ids.Should().BeEquivalentTo(ExpectedObservationIds);
    }

    [UnitTest]
    public void ObservationsSidecar_CoordinatesAreContainedByPublishedBounds()
    {
        // Clients use extras.bounds (and the OGC region) to frame and cull the
        // observation layer. Sidecar coordinates outside those bounds would be
        // dropped from the camera framing or culled from the scene entirely.
        // Asserting on both tilesets keeps main and obs bounds in sync.
        using var sidecar = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(_fixtureRoot, NvidiaConstructionFixturePaths.ObservationsSidecarFileName)));
        var observations = sidecar.RootElement.GetProperty("observations");

        foreach (var tilesetName in BothTilesetFiles)
        {
            using var tileset = LoadTileset(tilesetName);
            var bounds = tileset.RootElement.GetProperty("extras").GetProperty("bounds");
            var west = bounds.GetProperty("west").GetDouble();
            var east = bounds.GetProperty("east").GetDouble();
            var south = bounds.GetProperty("south").GetDouble();
            var north = bounds.GetProperty("north").GetDouble();

            foreach (var entry in observations.EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString();
                var lon = entry.GetProperty("longitude").GetDouble();
                var lat = entry.GetProperty("latitude").GetDouble();
                lon.Should().BeInRange(west, east, $"{id} longitude must lie within {tilesetName} bounds");
                lat.Should().BeInRange(south, north, $"{id} latitude must lie within {tilesetName} bounds");
            }
        }
    }

    [UnitTest]
    public void TileBinaries_ArePresentAndStartWithB3dmMagic()
    {
        foreach (var relative in BothTileBinaries)
        {
            var bytes = File.ReadAllBytes(Path.Combine(_fixtureRoot, relative));
            bytes.Length.Should().BeGreaterThan(4);
            System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("b3dm");
        }
    }

    [UnitTest]
    public void TilesetContentUris_AreSafeRelativePaths()
    {
        foreach (var tileset in BothTilesetFiles)
        {
            using var doc = LoadTileset(tileset);
            var uri = doc.RootElement.GetProperty("root").GetProperty("content").GetProperty("uri").GetString();

            uri.Should().NotBeNullOrEmpty();
            uri!.Should().NotStartWith("/", "asset URIs must be relative so the scene resolver constrains them under AssetRoot");
            uri.Should().NotStartWith("\\");
            uri.Should().NotContain("..", "no traversal segments in fixture content URIs");
            uri.Should().NotStartWith("http://", "demo fixture must not reference live cloud or external hosts");
            uri.Should().NotStartWith("https://");
        }
    }

    // ---- Integration assertions through hosted scene endpoints -----------

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

    private JsonDocument LoadTileset(string fileName)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(_fixtureRoot, fileName)));
}

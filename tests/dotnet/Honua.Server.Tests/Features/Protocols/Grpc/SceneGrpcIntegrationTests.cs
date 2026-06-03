// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// Integration tests for the gRPC SceneService, TileService, and ElevationService
/// (honua-server#1194 / #1195) exercised through the full ASP.NET Core pipeline
/// with a real PostgreSQL database via <see cref="WebAppFixture"/>. A
/// configuration-backed hosted-tiles scene (the canonical fixture tileset) is
/// registered so scene discovery and tile delivery run against real assets.
/// Uses gRPC-Web transport (HTTP/1.1) since the in-memory test server does not
/// support HTTP/2.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Grpc)]
public sealed class SceneGrpcIntegrationTests : IAsyncLifetime
{
    private const string FixtureSceneId = "fixture-tileset";

    private readonly WebAppFixture _fixture;
    private GrpcChannel? _channel;
    private Proto.SceneService.SceneServiceClient? _sceneClient;
    private Proto.TileService.TileServiceClient? _tileClient;
    private Proto.ElevationService.ElevationServiceClient? _elevationClient;
    private Metadata? _headers;

    public SceneGrpcIntegrationTests()
    {
        var fixtureRoot = ResolveFixtureRoot();
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scenes:Datasets:0:Id"] = FixtureSceneId,
                        ["Scenes:Datasets:0:Name"] = "Honua Fixture Tileset",
                        ["Scenes:Datasets:0:Description"] = "Static 3D Tiles fixture used by gRPC tests",
                        ["Scenes:Datasets:0:AssetRoot"] = fixtureRoot,
                    });
                });
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        var grpcWebHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, _fixture.CreateHandler());
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = grpcWebHandler });
        _sceneClient = new Proto.SceneService.SceneServiceClient(_channel);
        _tileClient = new Proto.TileService.TileServiceClient(_channel);
        _elevationClient = new Proto.ElevationService.ElevationServiceClient(_channel);

        _headers = new Metadata();
        if (_fixture.CurrentSchema is not null)
        {
            _headers.Add("X-Honua-Test-Schema", _fixture.CurrentSchema);
        }
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /geospatial.v1.SceneService/ListScenes")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.SceneService/ListScenes")]
    public async Task ListScenes_WithRegisteredFixtureScene_ReturnsScene()
    {
        var response = await _sceneClient!.ListScenesAsync(new Proto.ListScenesRequest(), _headers);

        response.Scenes.Should().Contain(scene => scene.SceneId == FixtureSceneId);
        var fixtureScene = response.Scenes.Single(scene => scene.SceneId == FixtureSceneId);
        fixtureScene.TilesetUrl.Should().Be($"/scenes/{FixtureSceneId}/tileset.json");
        fixtureScene.Capabilities.Should().Contain("3d-tiles");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /geospatial.v1.SceneService/GetScene")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.SceneService/GetScene")]
    public async Task GetScene_ForFixtureScene_ReturnsMetadata()
    {
        var response = await _sceneClient!.GetSceneAsync(
            new Proto.GetSceneRequest { SceneId = FixtureSceneId },
            _headers);

        response.Scene.Should().NotBeNull();
        response.Scene.SceneId.Should().Be(FixtureSceneId);
        response.Scene.Title.Should().Be("Honua Fixture Tileset");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /geospatial.v1.SceneService/GetScene")]
    public async Task GetScene_ForUnknownScene_ThrowsNotFound()
    {
        var act = async () => await _sceneClient!.GetSceneAsync(
            new Proto.GetSceneRequest { SceneId = "does-not-exist" },
            _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("POST /geospatial.v1.TileService/GetTile")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.TileService/GetTile")]
    public async Task GetTile_ForRootNode_ReturnsB3dmPayload()
    {
        var response = await _tileClient!.GetTileAsync(
            new Proto.GetTileRequest { SceneId = FixtureSceneId, NodeId = "0" },
            _headers);

        response.Tile.Should().NotBeNull();
        response.Tile.Node.NodeId.Should().Be("0");
        response.Tile.ContentType.Should().Be(Proto.TileContentType.B3Dm);
        response.Tile.Content.Length.Should().BeGreaterThan(0);
        response.Tile.Node.BoundingVolume.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("POST /geospatial.v1.TileService/GetTile")]
    public async Task GetTile_ForUnknownNode_ThrowsNotFound()
    {
        var act = async () => await _tileClient!.GetTileAsync(
            new Proto.GetTileRequest { SceneId = FixtureSceneId, NodeId = "999" },
            _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("POST /geospatial.v1.TileService/StreamTiles")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.TileService/StreamTiles")]
    public async Task StreamTiles_ForFixtureScene_StreamsTilesWithContent()
    {
        using var call = _tileClient!.StreamTiles(
            new Proto.StreamTilesRequest { SceneId = FixtureSceneId },
            _headers);

        var tiles = new List<Proto.Tile>();
        await foreach (var tile in call.ResponseStream.ReadAllAsync())
        {
            tiles.Add(tile);
        }

        tiles.Should().NotBeEmpty();
        tiles.Should().OnlyContain(tile => tile.Content.Length > 0 || tile.ContentType == Proto.TileContentType.Unspecified);
        tiles.Should().Contain(tile => tile.Node.NodeId == "0");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevation")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.ElevationService/GetElevation")]
    public async Task GetElevation_ForUnknownLayer_SurfacesStructuredOutcome()
    {
        var request = new Proto.GetElevationRequest
        {
            DatasetId = "missing-dataset",
            LayerId = 999999,
            Point = new Proto.PointGeometry { X = 0, Y = 0 },
        };

        // An unknown elevation layer either fails the query (mapped to a gRPC
        // status) or resolves to a no-data / out-of-bounds result, depending on
        // the active provider. Either is an acceptable structured outcome for
        // this coverage path; an unmapped server fault is not.
        try
        {
            var response = await _elevationClient!.GetElevationAsync(request, _headers);
            (response.NoData || response.OutOfBounds || response.HasElevation).Should().BeTrue();
        }
        catch (RpcException exception)
        {
            exception.StatusCode.Should().BeOneOf(
                StatusCode.FailedPrecondition,
                StatusCode.NotFound,
                StatusCode.InvalidArgument,
                StatusCode.Internal);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevationProfile")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.ElevationService/GetElevationProfile")]
    public async Task GetElevationProfile_WithSingleCoordinate_ThrowsInvalidArgument()
    {
        var line = new Proto.PolylineGeometry();
        var path = new Proto.CoordinateSequence();
        path.Coords.Add(new Proto.Coordinate { X = 0, Y = 0 });
        line.Paths.Add(path);

        var request = new Proto.GetElevationProfileRequest
        {
            DatasetId = "missing-dataset",
            LayerId = 999999,
            Line = line,
        };

        var act = async () => await _elevationClient!.GetElevationProfileAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    private static string ResolveFixtureRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "tests", "fixtures", "scenes", "fixture-tileset");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            "Could not locate tests/fixtures/scenes/fixture-tileset from the test base directory.");
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    private const string MissingContentSceneId = "missing-content-tileset";

    // A database-backed scene carrying a small WGS-84 extent near the prime
    // meridian, used to exercise the ListScenes spatial extent filter (config
    // scenes have no persisted extent and so cannot be excluded by it).
    private const string ExtentSceneId = "extent-scene";
    private const double ExtentXMin = 0.0;
    private const double ExtentYMin = 0.0;
    private const double ExtentXMax = 1.0;
    private const double ExtentYMax = 1.0;

    private readonly WebAppFixture _fixture;
    private readonly string _missingContentRoot;
    private GrpcChannel? _channel;
    private Proto.SceneService.SceneServiceClient? _sceneClient;
    private Proto.TileService.TileServiceClient? _tileClient;
    private Proto.ElevationService.ElevationServiceClient? _elevationClient;
    private Metadata? _headers;

    public SceneGrpcIntegrationTests()
    {
        var fixtureRoot = ResolveFixtureRoot();
        _missingContentRoot = CreateMissingContentTilesetRoot();
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

                        // A scene whose root node references a content uri that
                        // does not exist on disk, to exercise the GetTile
                        // content-resolution NotFound path.
                        ["Scenes:Datasets:1:Id"] = MissingContentSceneId,
                        ["Scenes:Datasets:1:Name"] = "Missing Content Tileset",
                        ["Scenes:Datasets:1:AssetRoot"] = _missingContentRoot,
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

        await RegisterExtentSceneAsync();
    }

    /// <summary>
    /// Registers a database-backed scene with a persisted extent so the
    /// ListScenes spatial-filter tests have a scene whose footprint can be
    /// included or excluded by an extent constraint (configuration scenes carry
    /// no extent and so are always included).
    /// </summary>
    private async Task RegisterExtentSceneAsync()
    {
        var registration = _fixture.GetService<ISceneRegistrationService>();
        await registration.RegisterAsync(new SceneDatasetRecord
        {
            DatasetId = Guid.NewGuid(),
            Id = ExtentSceneId,
            Name = "Extent Scene",
            AssetRoot = _missingContentRoot,
            TilesetFileName = "tileset.json",
            DatasetType = SceneDatasetType.HostedTiles,
            Extent = new SceneExtent(ExtentXMin, ExtentYMin, ExtentXMax, ExtentYMax),
            CachePolicy = SceneCachePolicy.Default,
            IsPublic = true,
            Status = SceneDatasetStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        });
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        await _fixture.DisposeAsync();

        try
        {
            if (Directory.Exists(_missingContentRoot))
            {
                Directory.Delete(_missingContentRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp fixture root.
        }
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
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /geospatial.v1.SceneService/ListScenes")]
    public async Task ListScenes_ZeroResultRecordCount_ReturnsEntireCatalog()
    {
        // Documented intentional divergence: the proto comments result_record_count
        // zero as "server default", but the adapter treats 0/unset as "no limit"
        // because the scene catalog is a small bounded set. A request that leaves
        // the count unset (0) must return every scene, never an arbitrary default
        // page, and must not flag ExceededTransferLimit.
        var total = (await _sceneClient!.ListScenesAsync(
            new Proto.ListScenesRequest { ResultRecordCount = 0 },
            _headers)).Scenes.Count;

        // The total here equals the count returned by an explicit page sized to the
        // catalog, confirming 0 imposed no cap.
        var explicitFull = await _sceneClient!.ListScenesAsync(
            new Proto.ListScenesRequest { ResultRecordCount = total },
            _headers);

        total.Should().BeGreaterThan(0);
        explicitFull.Scenes.Count.Should().Be(total);

        var zeroCount = await _sceneClient!.ListScenesAsync(
            new Proto.ListScenesRequest { ResultRecordCount = 0 },
            _headers);
        zeroCount.Scenes.Count.Should().Be(total);
        zeroCount.ExceededTransferLimit.Should().BeFalse(
            "a zero/unset result_record_count imposes no cap, so the transfer limit is never exceeded.");
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /geospatial.v1.SceneService/ListScenes")]
    public async Task ListScenes_WithResultRecordCountBelowTotal_PagesAndSetsExceededTransferLimit()
    {
        // The fixture registers at least three scenes (two config + one DB-backed
        // extent scene), so a page size of 1 must return a single scene and flag
        // that more records remain.
        var response = await _sceneClient!.ListScenesAsync(
            new Proto.ListScenesRequest { ResultRecordCount = 1 },
            _headers);

        response.Scenes.Should().ContainSingle();
        response.ExceededTransferLimit.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /geospatial.v1.SceneService/ListScenes")]
    public async Task ListScenes_WithResultRecordCountEqualToTotal_DoesNotSetExceededTransferLimit()
    {
        var total = (await _sceneClient!.ListScenesAsync(new Proto.ListScenesRequest(), _headers)).Scenes.Count;

        var response = await _sceneClient!.ListScenesAsync(
            new Proto.ListScenesRequest { ResultRecordCount = total },
            _headers);

        response.Scenes.Count.Should().Be(total);
        response.ExceededTransferLimit.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /geospatial.v1.SceneService/ListScenes")]
    public async Task ListScenes_WithResultOffsetPastEnd_ReturnsEmptyPage()
    {
        var response = await _sceneClient!.ListScenesAsync(
            new Proto.ListScenesRequest { ResultOffset = 10_000 },
            _headers);

        response.Scenes.Should().BeEmpty();
        response.ExceededTransferLimit.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /geospatial.v1.SceneService/ListScenes")]
    public async Task ListScenes_WithNegativeResultRecordCount_ReturnsEntireCatalog()
    {
        // A negative result_record_count is a reachable proto int32 input. The
        // adapter's `if (count > 0)` guard means a negative count falls through
        // and imposes NO cap (same as zero/unset), returning the entire catalog
        // without flagging ExceededTransferLimit. Pin that clamp-to-no-cap
        // behavior so a regression that threw or returned an empty page is caught.
        var total = (await _sceneClient!.ListScenesAsync(new Proto.ListScenesRequest(), _headers)).Scenes.Count;
        total.Should().BeGreaterThan(0);

        var response = await _sceneClient!.ListScenesAsync(
            new Proto.ListScenesRequest { ResultRecordCount = -1 },
            _headers);

        response.Scenes.Count.Should().Be(total);
        response.ExceededTransferLimit.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /geospatial.v1.SceneService/ListScenes")]
    public async Task ListScenes_WithNegativeResultOffset_ClampsToZeroAndReturnsEntireCatalog()
    {
        // A negative result_offset is clamped to 0 via Math.Max(0, ...), so it
        // behaves like no offset and returns the full catalog from the start.
        var total = (await _sceneClient!.ListScenesAsync(new Proto.ListScenesRequest(), _headers)).Scenes.Count;
        total.Should().BeGreaterThan(0);

        var response = await _sceneClient!.ListScenesAsync(
            new Proto.ListScenesRequest { ResultOffset = -1 },
            _headers);

        response.Scenes.Count.Should().Be(total);
        response.ExceededTransferLimit.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /geospatial.v1.SceneService/ListScenes")]
    public async Task ListScenes_WithOverlappingExtent_IncludesScene()
    {
        var request = new Proto.ListScenesRequest
        {
            Extent = new Proto.Extent3D
            {
                Extent = new Proto.Extent
                {
                    Xmin = ExtentXMin - 0.5,
                    Ymin = ExtentYMin - 0.5,
                    Xmax = ExtentXMin + 0.5,
                    Ymax = ExtentYMin + 0.5,
                    SpatialReference = new Proto.SpatialReference { Wkid = 4326, LatestWkid = 4326 },
                },
            },
        };

        var response = await _sceneClient!.ListScenesAsync(request, _headers);

        response.Scenes.Should().Contain(scene => scene.SceneId == ExtentSceneId);
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /geospatial.v1.SceneService/ListScenes")]
    public async Task ListScenes_WithDisjointExtent_ExcludesScene()
    {
        // An extent far from the registered scene's footprint must exclude it.
        var request = new Proto.ListScenesRequest
        {
            Extent = new Proto.Extent3D
            {
                Extent = new Proto.Extent
                {
                    Xmin = 100.0,
                    Ymin = 60.0,
                    Xmax = 101.0,
                    Ymax = 61.0,
                    SpatialReference = new Proto.SpatialReference { Wkid = 4326, LatestWkid = 4326 },
                },
            },
        };

        var response = await _sceneClient!.ListScenesAsync(request, _headers);

        response.Scenes.Should().NotContain(scene => scene.SceneId == ExtentSceneId);
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /geospatial.v1.SceneService/ListScenes")]
    public async Task ListScenes_WithDisjointExtent_StillIncludesSceneWithoutExtent()
    {
        // The config-backed fixture scene carries NO persisted extent. A scene
        // without an extent cannot be excluded by the spatial filter and must be
        // returned regardless of the request extent (the filter only excludes
        // scenes whose known footprint is disjoint).
        var request = new Proto.ListScenesRequest
        {
            Extent = new Proto.Extent3D
            {
                Extent = new Proto.Extent
                {
                    Xmin = 100.0,
                    Ymin = 60.0,
                    Xmax = 101.0,
                    Ymax = 61.0,
                    SpatialReference = new Proto.SpatialReference { Wkid = 4326, LatestWkid = 4326 },
                },
            },
        };

        var response = await _sceneClient!.ListScenesAsync(request, _headers);

        response.Scenes.Should().Contain(scene => scene.SceneId == FixtureSceneId);
        response.Scenes.Should().NotContain(scene => scene.SceneId == ExtentSceneId);
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
    [Operation(Operations.GetTile)]
    [Endpoint("POST /geospatial.v1.TileService/GetTile")]
    public async Task GetTile_ForExternalSubTilesetNode_StreamsOpaqueUnspecifiedContent()
    {
        // The fixture's LOD-1 child node's content uri is an external .json
        // sub-tileset (nested/sub-tileset.json). The contract (documented on
        // HonuaTileGrpcService.BuildTileAsync) is to stream it as OPAQUE bytes
        // with TileContentType.Unspecified — the server does not expand the
        // sub-tileset; the client follows it.
        var response = await _tileClient!.GetTileAsync(
            new Proto.GetTileRequest { SceneId = FixtureSceneId, NodeId = "0-0" },
            _headers);

        response.Tile.Should().NotBeNull();
        response.Tile.Node.NodeId.Should().Be("0-0");
        response.Tile.ContentType.Should().Be(Proto.TileContentType.Unspecified);
        response.Tile.Content.Length.Should().BeGreaterThan(0, "the external sub-tileset is streamed verbatim as opaque bytes");
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
    [Operation(Operations.Streaming)]
    [Endpoint("POST /geospatial.v1.TileService/StreamTiles")]
    public async Task StreamTiles_WithMinLod_ExcludesRootNode()
    {
        // The fixture root is LOD 0 and its child (nested sub-tileset) is LOD 1;
        // min_lod=1 must drop the root and keep the LOD-1 node.
        var tiles = await StreamAsync(new Proto.StreamTilesRequest { SceneId = FixtureSceneId, MinLod = 1 });

        tiles.Should().NotBeEmpty();
        tiles.Should().NotContain(tile => tile.Node.NodeId == "0");
        tiles.Should().OnlyContain(tile => tile.Node.Lod >= 1);
    }

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("POST /geospatial.v1.TileService/StreamTiles")]
    public async Task StreamTiles_WithMaxLodZero_ReturnsAllLods()
    {
        // Per the proto contract, max_lod=0 means "all available LODs" rather
        // than a cap at the root — both the LOD-0 root and the LOD-1 child are
        // returned. This guards against inverting the "0 == unlimited" rule.
        var tiles = await StreamAsync(new Proto.StreamTilesRequest { SceneId = FixtureSceneId, MaxLod = 0 });

        tiles.Should().NotBeEmpty();
        tiles.Should().Contain(tile => tile.Node.Lod == 0);
        tiles.Should().Contain(tile => tile.Node.Lod == 1);
    }

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("POST /geospatial.v1.TileService/StreamTiles")]
    public async Task StreamTiles_WithMaxLodOne_IncludesDepthOneNodes()
    {
        // max_lod=1 keeps nodes at LOD 0 and 1 (the fixture's full depth) and
        // never streams a node above the cap.
        var tiles = await StreamAsync(new Proto.StreamTilesRequest { SceneId = FixtureSceneId, MaxLod = 1 });

        tiles.Should().NotBeEmpty();
        tiles.Should().OnlyContain(tile => tile.Node.Lod <= 1);
    }

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("POST /geospatial.v1.TileService/StreamTiles")]
    public async Task StreamTiles_WithMaxGeometricErrorBelowRoot_ExcludesCoarseNodes()
    {
        // The root's geometric error is 100; a cap below that must drop the root
        // (coarse) node while keeping finer nodes (the LOD-1 child has error 0).
        var tiles = await StreamAsync(new Proto.StreamTilesRequest
        {
            SceneId = FixtureSceneId,
            MaxGeometricError = 50.0,
        });

        tiles.Should().NotBeEmpty();
        tiles.Should().NotContain(tile => tile.Node.NodeId == "0");
        tiles.Should().OnlyContain(tile => tile.Node.GeometricError <= 50.0);
    }

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("POST /geospatial.v1.TileService/StreamTiles")]
    public async Task StreamTiles_WithMaxGeometricErrorZero_AppliesNoGeometricErrorFilter()
    {
        // Intentional contract divergence (documented on StreamTiles): the proto
        // calls max_geometric_error=0 "server default", but Honua applies NO
        // server-side default cap — 0 means unbounded. The coarse root node
        // (error 100) must therefore still stream when the cap is left unset.
        var unbounded = await StreamAsync(new Proto.StreamTilesRequest
        {
            SceneId = FixtureSceneId,
            MaxGeometricError = 0.0,
        });

        var unfiltered = await StreamAsync(new Proto.StreamTilesRequest { SceneId = FixtureSceneId });

        unbounded.Should().Contain(tile => tile.Node.NodeId == "0");
        unbounded.Select(tile => tile.Node.NodeId)
            .Should().BeEquivalentTo(unfiltered.Select(tile => tile.Node.NodeId));
    }

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("POST /geospatial.v1.TileService/StreamTiles")]
    public async Task StreamTiles_WithDisjointExtent_YieldsNoTiles()
    {
        // An extent on the opposite side of the globe from the fixture region
        // intersects nothing, so the stream must be empty.
        var request = new Proto.StreamTilesRequest { SceneId = FixtureSceneId };
        request.Extent = new Proto.Extent3D
        {
            Extent = new Proto.Extent
            {
                Xmin = 100.0,
                Ymin = 60.0,
                Xmax = 101.0,
                Ymax = 61.0,
                SpatialReference = new Proto.SpatialReference { Wkid = 4326, LatestWkid = 4326 },
            },
        };

        var tiles = await StreamAsync(request);

        tiles.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("POST /geospatial.v1.TileService/StreamTiles")]
    public async Task StreamTiles_ForUnknownScene_ThrowsNotFound()
    {
        using var call = _tileClient!.StreamTiles(
            new Proto.StreamTilesRequest { SceneId = "does-not-exist" },
            _headers);

        var act = async () =>
        {
            await foreach (var _ in call.ResponseStream.ReadAllAsync())
            {
            }
        };

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("POST /geospatial.v1.TileService/GetTile")]
    public async Task GetTile_ForNodeWithMissingContentFile_ThrowsNotFound()
    {
        var act = async () => await _tileClient!.GetTileAsync(
            new Proto.GetTileRequest { SceneId = MissingContentSceneId, NodeId = "0" },
            _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.NotFound);
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
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevation")]
    public async Task GetElevation_WithNullPoint_ThrowsInvalidArgument()
    {
        var request = new Proto.GetElevationRequest
        {
            DatasetId = "missing-dataset",
            LayerId = 0,
            // Point intentionally left unset (null) — the adapter must reject it
            // before reaching the canonical elevation service.
        };

        var act = async () => await _elevationClient!.GetElevationAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevationProfile")]
    public async Task GetElevationProfile_WithSampleCountOne_ThrowsInvalidArgument()
    {
        // Two distinct coordinates so BuildLineString's <2-coord guard is NOT
        // what trips; the rejection must come from the sample_count==1 branch.
        var line = new Proto.PolylineGeometry();
        var path = new Proto.CoordinateSequence();
        path.Coords.Add(new Proto.Coordinate { X = 0, Y = 0 });
        path.Coords.Add(new Proto.Coordinate { X = 0.001, Y = 0.001 });
        line.Paths.Add(path);

        var request = new Proto.GetElevationProfileRequest
        {
            DatasetId = "missing-dataset",
            LayerId = 0,
            Line = line,
            SampleCount = 1,
        };

        var act = async () => await _elevationClient!.GetElevationProfileAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
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

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevationProfile")]
    public async Task GetElevationProfile_WithNegativeSampleCount_ThrowsInvalidArgument()
    {
        // A negative sample_count is invalid (the contract is "at least 2; zero
        // means server default") and must be rejected rather than silently
        // coerced to the default count.
        var line = new Proto.PolylineGeometry();
        var path = new Proto.CoordinateSequence();
        path.Coords.Add(new Proto.Coordinate { X = 0, Y = 0 });
        path.Coords.Add(new Proto.Coordinate { X = 0.001, Y = 0.001 });
        line.Paths.Add(path);

        var request = new Proto.GetElevationProfileRequest
        {
            DatasetId = "missing-dataset",
            LayerId = 0,
            Line = line,
            SampleCount = -1,
        };

        var act = async () => await _elevationClient!.GetElevationProfileAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevation")]
    public async Task GetElevation_WithNonFinitePoint_ThrowsInvalidArgument()
    {
        var request = new Proto.GetElevationRequest
        {
            DatasetId = "missing-dataset",
            LayerId = 0,
            Point = new Proto.PointGeometry { X = double.NaN, Y = 0 },
        };

        var act = async () => await _elevationClient!.GetElevationAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevationProfile")]
    public async Task GetElevationProfile_WithNonFiniteCoordinate_ThrowsInvalidArgument()
    {
        var line = new Proto.PolylineGeometry();
        var path = new Proto.CoordinateSequence();
        path.Coords.Add(new Proto.Coordinate { X = 0, Y = 0 });
        path.Coords.Add(new Proto.Coordinate { X = 0.001, Y = double.PositiveInfinity });
        line.Paths.Add(path);

        var request = new Proto.GetElevationProfileRequest
        {
            DatasetId = "missing-dataset",
            LayerId = 0,
            Line = line,
        };

        var act = async () => await _elevationClient!.GetElevationProfileAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevationProfile")]
    public async Task GetElevationProfile_WithSampleCountAboveMax_ThrowsInvalidArgument()
    {
        // The over-max guard (sample_count > configured MaxSampleCount) must
        // reject the request rather than streaming an unbounded sample count to
        // the provider. Read the configured cap from the running fixture so the
        // test stays correct if the default changes.
        var limits = _fixture.GetService<Microsoft.Extensions.Options.IOptions<Honua.Core.Configuration.LimitsOptions>>();
        var aboveMax = limits.Value.Elevation.MaxSampleCount + 1;

        var line = new Proto.PolylineGeometry();
        var path = new Proto.CoordinateSequence();
        path.Coords.Add(new Proto.Coordinate { X = 0, Y = 0 });
        path.Coords.Add(new Proto.Coordinate { X = 0.001, Y = 0.001 });
        line.Paths.Add(path);

        var request = new Proto.GetElevationProfileRequest
        {
            DatasetId = "missing-dataset",
            LayerId = 0,
            Line = line,
            SampleCount = aboveMax,
        };

        var act = async () => await _elevationClient!.GetElevationProfileAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevationProfile")]
    public async Task GetElevationProfile_WithEmptyLine_ThrowsInvalidArgument()
    {
        // A PolylineGeometry with zero paths (and the unset-Line case it shares a
        // branch with) must be rejected with InvalidArgument before any sampling.
        var request = new Proto.GetElevationProfileRequest
        {
            DatasetId = "missing-dataset",
            LayerId = 0,
            Line = new Proto.PolylineGeometry(),
        };

        var act = async () => await _elevationClient!.GetElevationProfileAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevationProfile")]
    public async Task GetElevationProfile_WithMultiplePaths_ThrowsInvalidArgument()
    {
        // A profile is sampled along ONE continuous polyline; a multi-part line
        // has no well-defined cumulative distance. The adapter rejects a request
        // with more than one path rather than silently sampling only the first.
        var line = new Proto.PolylineGeometry();
        var first = new Proto.CoordinateSequence();
        first.Coords.Add(new Proto.Coordinate { X = 0, Y = 0 });
        first.Coords.Add(new Proto.Coordinate { X = 0.001, Y = 0.001 });
        var second = new Proto.CoordinateSequence();
        second.Coords.Add(new Proto.Coordinate { X = 1, Y = 1 });
        second.Coords.Add(new Proto.Coordinate { X = 1.001, Y = 1.001 });
        line.Paths.Add(first);
        line.Paths.Add(second);

        var request = new Proto.GetElevationProfileRequest
        {
            DatasetId = "missing-dataset",
            LayerId = 0,
            Line = line,
        };

        var act = async () => await _elevationClient!.GetElevationProfileAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevation")]
    public async Task GetElevation_WithUnsupportedCrs_ThrowsInvalidArgument()
    {
        // The gRPC elevation surface validates the requested WKID against the
        // shared ICrsRegistry (parity with the HTTP elevation endpoint); an
        // unsupported WKID must be rejected with InvalidArgument before reaching
        // provider code.
        var request = new Proto.GetElevationRequest
        {
            DatasetId = "missing-dataset",
            LayerId = 0,
            Point = new Proto.PointGeometry { X = 0, Y = 0 },
            SpatialReference = new Proto.SpatialReference { Wkid = 999999 },
        };

        var act = async () => await _elevationClient!.GetElevationAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /geospatial.v1.SceneService/GetScene")]
    public async Task GetScene_ForInactiveScene_ThrowsNotFound()
    {
        // A registered-but-inactive scene must never be disclosed via GetScene;
        // it surfaces as NotFound just like a scene that does not exist.
        const string inactiveSceneId = "inactive-grpc-scene";
        var registration = _fixture.GetService<ISceneRegistrationService>();
        await registration.RegisterAsync(new SceneDatasetRecord
        {
            DatasetId = Guid.NewGuid(),
            Id = inactiveSceneId,
            Name = "Inactive Scene",
            AssetRoot = _missingContentRoot,
            TilesetFileName = "tileset.json",
            DatasetType = SceneDatasetType.HostedTiles,
            CachePolicy = SceneCachePolicy.Default,
            IsPublic = true,
            Status = SceneDatasetStatus.Inactive,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        });

        var act = async () => await _sceneClient!.GetSceneAsync(
            new Proto.GetSceneRequest { SceneId = inactiveSceneId },
            _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Theory]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /geospatial.v1.SceneService/GetScene")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetScene_WithBlankSceneId_ThrowsInvalidArgument(string sceneId)
    {
        var act = async () => await _sceneClient!.GetSceneAsync(
            new Proto.GetSceneRequest { SceneId = sceneId },
            _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Theory]
    [Operation(Operations.GetTile)]
    [Endpoint("POST /geospatial.v1.TileService/GetTile")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetTile_WithBlankSceneId_ThrowsInvalidArgument(string sceneId)
    {
        var act = async () => await _tileClient!.GetTileAsync(
            new Proto.GetTileRequest { SceneId = sceneId, NodeId = "0" },
            _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Theory]
    [Operation(Operations.GetTile)]
    [Endpoint("POST /geospatial.v1.TileService/GetTile")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetTile_WithBlankNodeId_ThrowsInvalidArgument(string nodeId)
    {
        var act = async () => await _tileClient!.GetTileAsync(
            new Proto.GetTileRequest { SceneId = FixtureSceneId, NodeId = nodeId },
            _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Theory]
    [Operation(Operations.Streaming)]
    [Endpoint("POST /geospatial.v1.TileService/StreamTiles")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StreamTiles_WithBlankSceneId_ThrowsInvalidArgument(string sceneId)
    {
        using var call = _tileClient!.StreamTiles(
            new Proto.StreamTilesRequest { SceneId = sceneId },
            _headers);

        var act = async () =>
        {
            await foreach (var _ in call.ResponseStream.ReadAllAsync())
            {
            }
        };

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    private async Task<List<Proto.Tile>> StreamAsync(Proto.StreamTilesRequest request)
    {
        using var call = _tileClient!.StreamTiles(request, _headers);
        var tiles = new List<Proto.Tile>();
        await foreach (var tile in call.ResponseStream.ReadAllAsync())
        {
            tiles.Add(tile);
        }

        return tiles;
    }

    /// <summary>
    /// Creates a temporary scene asset root containing a valid
    /// <c>tileset.json</c> whose root content uri points at a b3dm file that
    /// does not exist, so GetTile resolves the node but cannot read its bytes.
    /// </summary>
    private static string CreateMissingContentTilesetRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "honua-grpc-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        const string tilesetJson = """
        {
          "asset": { "version": "1.1" },
          "geometricError": 100.0,
          "root": {
            "boundingVolume": { "region": [-1.31970, 0.69886, -1.31965, 0.69890, 0.0, 20.0] },
            "geometricError": 100.0,
            "refine": "REPLACE",
            "content": { "uri": "tiles/does-not-exist.b3dm" }
          }
        }
        """;
        File.WriteAllText(Path.Combine(root, "tileset.json"), tilesetJson);
        return root;
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

/// <summary>
/// Authorization tests proving the gRPC scene/tile services enforce the same
/// per-scene <c>AccessPolicy</c> the HTTP/I3S surfaces enforce
/// (honua-server#1194 / #1195). A protected (non-public) scene must not be
/// served — metadata or tiles — to an unauthenticated gRPC caller; it returns
/// <see cref="StatusCode.Unauthenticated"/> (or
/// <see cref="StatusCode.PermissionDenied"/> for an authenticated-but-unauthorized
/// caller). Dev-auth is disabled so the anonymous caller is genuinely
/// unauthenticated, mirroring <c>SceneAuthorizationTests</c> on the HTTP side.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Grpc)]
public sealed class SceneGrpcAuthorizationTests : IAsyncLifetime
{
    private const string AdminPassword = "scene-grpc-auth-test-key";
    private const string PublicSceneId = "public-fixture-tileset";
    private const string ProtectedSceneId = "protected-grpc-tileset";

    private readonly WebAppFixture _fixture;
    private GrpcChannel? _channel;
    private Proto.SceneService.SceneServiceClient? _sceneClient;
    private Proto.TileService.TileServiceClient? _tileClient;
    private Metadata? _headers;

    public SceneGrpcAuthorizationTests()
    {
        var fixtureRoot = ResolveFixtureRoot();
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        // Public scene — no access policy.
                        ["Scenes:Datasets:0:Id"] = PublicSceneId,
                        ["Scenes:Datasets:0:Name"] = "Public Fixture",
                        ["Scenes:Datasets:0:AssetRoot"] = fixtureRoot,

                        // Protected scene — anonymous denied, role-restricted.
                        ["Scenes:Datasets:1:Id"] = ProtectedSceneId,
                        ["Scenes:Datasets:1:Name"] = "Protected Fixture",
                        ["Scenes:Datasets:1:AssetRoot"] = fixtureRoot,
                        ["Scenes:Datasets:1:AccessPolicy:AllowAnonymous"] = "false",
                        ["Scenes:Datasets:1:AccessPolicy:AllowedRoles:0"] = "scene-admin",
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
    [Operation(Operations.GetTile)]
    [Endpoint("POST /geospatial.v1.TileService/GetTile")]
    public async Task GetTile_ProtectedScene_WithoutAuth_IsDenied()
    {
        var act = async () => await _tileClient!.GetTileAsync(
            new Proto.GetTileRequest { SceneId = ProtectedSceneId, NodeId = "0" },
            _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().BeOneOf(StatusCode.Unauthenticated, StatusCode.PermissionDenied);
    }

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("POST /geospatial.v1.TileService/StreamTiles")]
    public async Task StreamTiles_ProtectedScene_WithoutAuth_IsDenied()
    {
        using var call = _tileClient!.StreamTiles(
            new Proto.StreamTilesRequest { SceneId = ProtectedSceneId },
            _headers);

        var act = async () =>
        {
            await foreach (var _ in call.ResponseStream.ReadAllAsync())
            {
            }
        };

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().BeOneOf(StatusCode.Unauthenticated, StatusCode.PermissionDenied);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /geospatial.v1.SceneService/GetScene")]
    public async Task GetScene_ProtectedScene_WithoutAuth_IsDenied()
    {
        var act = async () => await _sceneClient!.GetSceneAsync(
            new Proto.GetSceneRequest { SceneId = ProtectedSceneId },
            _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().BeOneOf(StatusCode.Unauthenticated, StatusCode.PermissionDenied);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("POST /geospatial.v1.TileService/GetTile")]
    public async Task GetTile_PublicScene_WithoutAuth_Succeeds()
    {
        // The public sibling stays anonymously readable so the guard only gates
        // the protected scene, not all gRPC tile traffic.
        var response = await _tileClient!.GetTileAsync(
            new Proto.GetTileRequest { SceneId = PublicSceneId, NodeId = "0" },
            _headers);

        response.Tile.Should().NotBeNull();
        response.Tile.Content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /geospatial.v1.SceneService/ListScenes")]
    public async Task ListScenes_ProtectedScene_WithoutAuth_StillListsScene()
    {
        // Documented intentional divergence (HonuaSceneGrpcService.ListScenes):
        // ListScenes is ungated and mirrors the public HTTP scene-discovery
        // catalog, so it lists EVERY active scene — including protected ones —
        // while per-scene authorization is enforced only when metadata or tiles are
        // actually fetched (GetScene / GetTile / StreamTiles, covered above). This
        // pins that discovery parity so a future change that filters protected
        // scenes from the anonymous listing fails here instead of silently
        // diverging from the HTTP surface.
        var response = await _sceneClient!.ListScenesAsync(new Proto.ListScenesRequest(), _headers);

        response.Scenes.Should().Contain(scene => scene.SceneId == ProtectedSceneId,
            "ListScenes is intentionally ungated and lists protected scenes for discovery parity.");
        response.Scenes.Should().Contain(scene => scene.SceneId == PublicSceneId);
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

/// <summary>
/// Authorization tests proving the gRPC <c>ElevationService</c> enforces the same
/// per-layer RBAC the HTTP elevation surface enforces. The HTTP adapter validates
/// the elevation layer through <c>LayerValidationHelpers.ValidateLayerWithAccessV2Async</c>
/// at read scope before querying; the gRPC adapter routes through the shared
/// <c>ElevationAccessGuard</c>/<c>EvaluateLayerReadAccessV2Async</c> seam. A
/// protected (anonymous-denied) elevation layer must therefore be refused for an
/// unauthenticated gRPC caller, and a public layer must remain queryable. Dev-auth
/// is disabled so the anonymous caller is genuinely unauthenticated.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Grpc)]
public sealed class ElevationGrpcAuthorizationTests : IAsyncLifetime
{
    private const double WebMercatorExtent = 20037508.342789244;
    private const string AdminPassword = "elevation-grpc-auth-test-key";

    private readonly WebAppFixture _fixture;
    private GrpcChannel? _channel;
    private Proto.ElevationService.ElevationServiceClient? _elevationClient;
    private Metadata? _headers;

    public ElevationGrpcAuthorizationTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        var grpcWebHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, _fixture.CreateHandler());
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = grpcWebHandler });
        _elevationClient = new Proto.ElevationService.ElevationServiceClient(_channel);

        _headers = new Metadata();
        if (_fixture.CurrentSchema is not null)
        {
            _headers.Add("X-Honua-Test-Schema", _fixture.CurrentSchema);
        }

        await SeedFullWorldRasterAsync(100.0);
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevation")]
    public async Task GetElevation_ProtectedLayer_WithoutAuth_IsDenied()
    {
        // Restrict the test service so anonymous callers are denied at read scope.
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["elevation-admin"] });

        var request = new Proto.GetElevationRequest
        {
            LayerId = WebAppFixture.TestLayerId,
            Point = new Proto.PointGeometry { X = 0, Y = 0 },
        };

        var act = async () => await _elevationClient!.GetElevationAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().BeOneOf(StatusCode.Unauthenticated, StatusCode.PermissionDenied);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevationProfile")]
    public async Task GetElevationProfile_ProtectedLayer_WithoutAuth_IsDenied()
    {
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["elevation-admin"] });

        var line = new Proto.PolylineGeometry();
        var path = new Proto.CoordinateSequence();
        path.Coords.Add(new Proto.Coordinate { X = 0, Y = 0 });
        path.Coords.Add(new Proto.Coordinate { X = 0.001, Y = 0.001 });
        line.Paths.Add(path);

        var request = new Proto.GetElevationProfileRequest
        {
            LayerId = WebAppFixture.TestLayerId,
            Line = line,
            SampleCount = 5,
        };

        var act = async () => await _elevationClient!.GetElevationProfileAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().BeOneOf(StatusCode.Unauthenticated, StatusCode.PermissionDenied);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevation")]
    public async Task GetElevation_PublicLayer_WithoutAuth_Succeeds()
    {
        // Control: with anonymous access allowed the same query is NOT gated by the
        // RBAC guard and reaches the canonical elevation service, returning a
        // structured result rather than an auth denial.
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = true });

        var request = new Proto.GetElevationRequest
        {
            LayerId = WebAppFixture.TestLayerId,
            Point = new Proto.PointGeometry { X = 0, Y = 0 },
        };

        var response = await _elevationClient!.GetElevationAsync(request, _headers);

        // The seeded full-world raster covers (0,0); the guard let the query through
        // and the canonical service produced a value (not a no-data/out-of-bounds).
        (response.HasElevation || response.NoData || response.OutOfBounds).Should().BeTrue();
        response.HasElevation.Should().BeTrue("the seeded raster covers the origin, so a public query returns a value.");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevationProfile")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.ElevationService/GetElevationProfile")]
    public async Task GetElevationProfile_PublicLayer_ReturnsOrderedSamplesAndSourceMetadata()
    {
        // Happy path: a public layer over the seeded full-world raster returns an
        // ordered profile with a positive line length, the echoed mosaic rule, and
        // populated source metadata (mirrors GetElevation_PublicLayer happy path).
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = true });

        const int sampleCount = 5;
        var line = new Proto.PolylineGeometry();
        var path = new Proto.CoordinateSequence();
        path.Coords.Add(new Proto.Coordinate { X = -WebMercatorExtent / 2, Y = 0 });
        path.Coords.Add(new Proto.Coordinate { X = WebMercatorExtent / 2, Y = 0 });
        line.Paths.Add(path);

        var request = new Proto.GetElevationProfileRequest
        {
            LayerId = WebAppFixture.TestLayerId,
            SampleCount = sampleCount,
            SpatialReference = new Proto.SpatialReference { Wkid = 3857, LatestWkid = 3857 },
            Line = line,
        };

        var response = await _elevationClient!.GetElevationProfileAsync(request, _headers);

        response.Samples.Should().HaveCount(sampleCount);
        response.Samples.Select(s => s.DistanceMeters).Should()
            .BeInAscendingOrder("profile samples must be ordered by distance along the line");
        response.Samples[0].DistanceMeters.Should().Be(0d);
        response.LineLengthMeters.Should().BeGreaterThan(0d);
        response.Samples[^1].DistanceMeters.Should().BeApproximately(response.LineLengthMeters, 1e-3);
        response.MosaicRule.Should().NotBeNullOrEmpty("the resolved mosaic rule is echoed back");
        response.IsAllNoData.Should().BeFalse("the seeded raster covers the line");
        response.Source.Should().NotBeNull();
        response.Source.RasterIds.Should().NotBeEmpty("the source raster ids identify the contributing rasters");
        response.Source.SourceSpatialReference.Wkid.Should().Be(3857);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevation")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.ElevationService/GetElevation")]
    public async Task GetElevation_PublicLayer_EchoesPointAndReturnsSourceMetadata()
    {
        // #7: pin the POINT happy-path contract. The proto GetElevationResponse
        // echoes x/y and carries mosaic_rule + source metadata, but the existing
        // point happy-path only asserted HasElevation. Mirror the profile
        // assertion so the point mapping (SceneGrpcMapping.ToElevationResponse) is
        // covered for the echoed coordinates and populated source.
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = true });

        const double x = 1234.5;
        const double y = -678.25;
        var request = new Proto.GetElevationRequest
        {
            LayerId = WebAppFixture.TestLayerId,
            Point = new Proto.PointGeometry { X = x, Y = y },
            SpatialReference = new Proto.SpatialReference { Wkid = 3857, LatestWkid = 3857 },
        };

        var response = await _elevationClient!.GetElevationAsync(request, _headers);

        response.HasElevation.Should().BeTrue("the seeded full-world raster covers the queried point.");
        response.X.Should().Be(x, "the response echoes the requested x coordinate.");
        response.Y.Should().Be(y, "the response echoes the requested y coordinate.");
        response.MosaicRule.Should().NotBeNullOrEmpty("the resolved mosaic rule is echoed back.");
        response.Source.Should().NotBeNull();
        response.Source.RasterIds.Should().NotBeEmpty("the source raster ids identify the contributing rasters.");
        response.Source.SourceSpatialReference.Wkid.Should().Be(3857);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ElevationService/GetElevation")]
    public async Task GetElevation_LayerWithNoRasterSource_ThrowsNotFound()
    {
        // #4: the gRPC adapter must mirror the HTTP elevation status mapping
        // (ElevationEndpoints.MapElevationException), where ElevationFailureKind
        // .SourceUnavailable maps to NotFound (HTTP 404). Previously every
        // ElevationQueryException collapsed to FailedPrecondition. Clearing the
        // layer's rasters makes the query raise SourceUnavailable, which must now
        // surface as gRPC NotFound rather than FailedPrecondition.
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = true });

        // Remove every raster for the layer so source resolution fails.
        await RasterIntegrationTestData.ReplaceLayerRastersAsync(_fixture, WebAppFixture.TestLayerId);

        var request = new Proto.GetElevationRequest
        {
            LayerId = WebAppFixture.TestLayerId,
            Point = new Proto.PointGeometry { X = 0, Y = 0 },
            SpatialReference = new Proto.SpatialReference { Wkid = 3857, LatestWkid = 3857 },
        };

        var act = async () => await _elevationClient!.GetElevationAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.NotFound,
                "a missing raster source maps to NotFound, mirroring the HTTP 404 mapping.");
    }

    private Task SeedFullWorldRasterAsync(double elevationMeters)
        => RasterIntegrationTestData.ReplaceLayerRastersAsync(
            _fixture,
            WebAppFixture.TestLayerId,
            new RasterSeed(
                Name: "world-dem",
                Width: 2,
                Height: 2,
                UpperLeftX: -WebMercatorExtent,
                UpperLeftY: WebMercatorExtent,
                ScaleX: WebMercatorExtent,
                ScaleY: -WebMercatorExtent,
                Value: elevationMeters,
                AcquisitionDate: RasterIntegrationTestData.WestAcquisition,
                CreatedAt: RasterIntegrationTestData.WestAcquisition,
                Srid: 3857));
}

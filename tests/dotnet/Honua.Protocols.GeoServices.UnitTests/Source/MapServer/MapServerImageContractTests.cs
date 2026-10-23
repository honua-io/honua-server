// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Rendering;
using Honua.Protocols.GeoServices.MapServer;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

/// <summary>
/// Locks the MapServer tile-cache, output-SR, and synchronous exportTiles contracts.
/// </summary>
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerImageContractTests
{
    [UnitTest]
    [Operation(Operations.Tile)]
    public void BuildMapServerTileCacheKey_GenerationChange_ChangesKey()
    {
        var unchanged = BuildKey(generation: 0);
        var edited = BuildKey(generation: 2);

        edited.Should().NotBe(unchanged);
    }

    [UnitTest]
    [Operation(Operations.Tile)]
    public async Task ResolveTileCacheGeneration_DisabledCache_DoesNotQueryTheChangeLog()
    {
        MapServerEndpoints.ResetTileGenerationCacheForTests();
        var tracker = Tracker(9);
        var clock = new ManualTimeProvider();

        var missingStore = await MapServerEndpoints.ResolveTileCacheGenerationAsync(
            storage: null,
            new CloudStorageOptions { Enabled = true },
            tracker,
            clock,
            CancellationToken.None);
        var disabled = await MapServerEndpoints.ResolveTileCacheGenerationAsync(
            Substitute.For<ICloudFileStorage>(),
            new CloudStorageOptions { Enabled = false },
            tracker,
            clock,
            CancellationToken.None);

        missingStore.Should().Be(0);
        disabled.Should().Be(0);
        await tracker.DidNotReceive().GetCurrentGenerationAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Tile)]
    public async Task ResolveTileCacheGeneration_ReusesAFreshObservationAndSurvivesADatabaseOutage()
    {
        MapServerEndpoints.ResetTileGenerationCacheForTests();
        var clock = new ManualTimeProvider();
        var storage = Substitute.For<ICloudFileStorage>();
        var options = new CloudStorageOptions { Enabled = true };
        var observed = Tracker(4);
        var newer = Tracker(11);
        var down = TrackerThrowing(new IOException("database unavailable"));

        var first = await MapServerEndpoints.ResolveTileCacheGenerationAsync(
            storage, options, observed, clock, CancellationToken.None);
        var duringFreshness = await MapServerEndpoints.ResolveTileCacheGenerationAsync(
            storage, options, newer, clock, CancellationToken.None);

        clock.Now = clock.Now.AddSeconds(3);
        var afterOutage = await MapServerEndpoints.ResolveTileCacheGenerationAsync(
            storage, options, down, clock, CancellationToken.None);
        var stillHeld = await MapServerEndpoints.ResolveTileCacheGenerationAsync(
            storage, options, newer, clock, CancellationToken.None);

        first.Should().Be(4);
        duringFreshness.Should().Be(4);
        afterOutage.Should().Be(4);
        stillHeld.Should().Be(4);
        await observed.Received(1).GetCurrentGenerationAsync(Arg.Any<CancellationToken>());
        await newer.DidNotReceive().GetCurrentGenerationAsync(Arg.Any<CancellationToken>());
        await down.Received(1).GetCurrentGenerationAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Tile)]
    public async Task ResolveTileCacheGeneration_ColdDatabaseOutage_KeepsTheStableKeyWithoutRetrying()
    {
        MapServerEndpoints.ResetTileGenerationCacheForTests();
        var clock = new ManualTimeProvider();
        var storage = Substitute.For<ICloudFileStorage>();
        var options = new CloudStorageOptions { Enabled = true };
        var down = TrackerThrowing(new IOException("database unavailable"));
        var recovered = Tracker(8);

        var first = await MapServerEndpoints.ResolveTileCacheGenerationAsync(
            storage, options, down, clock, CancellationToken.None);
        var held = await MapServerEndpoints.ResolveTileCacheGenerationAsync(
            storage, options, recovered, clock, CancellationToken.None);

        first.Should().Be(0);
        held.Should().Be(0);
        await down.Received(1).GetCurrentGenerationAsync(Arg.Any<CancellationToken>());
        await recovered.DidNotReceive().GetCurrentGenerationAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public void TryResolveMapServerRequestSrid_OmittedSr_UsesAdvertisedWebMercator()
    {
        var resolved = MapServerEndpoints.TryResolveMapServerRequestSrid(
            srValue: null,
            geometryLatestWkid: null,
            geometryWkid: null,
            serviceSrid: 4326,
            out var srid,
            out var error);

        resolved.Should().BeTrue();
        error.Should().BeNull();
        srid.Should().Be(3857);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public void TryResolveMapServerRequestSrid_ExplicitSr_WinsOverGeometryAndService()
    {
        var resolved = MapServerEndpoints.TryResolveMapServerRequestSrid(
            "4326",
            geometryLatestWkid: 3857,
            geometryWkid: 4269,
            serviceSrid: 3857,
            out var srid,
            out var error);

        resolved.Should().BeTrue();
        error.Should().BeNull();
        srid.Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Find)]
    public void TryResolveMapServerRequestSrid_GeometryWkid_UsedWhenSrOmitted()
    {
        MapServerEndpoints.TryResolveMapServerRequestSrid(
            srValue: null,
            geometryLatestWkid: 32610,
            geometryWkid: 4269,
            serviceSrid: 4326,
            out var latest,
            out _).Should().BeTrue();
        latest.Should().Be(32610);

        MapServerEndpoints.TryResolveMapServerRequestSrid(
            srValue: "  ",
            geometryLatestWkid: null,
            geometryWkid: 4269,
            serviceSrid: 4326,
            out var wkid,
            out _).Should().BeTrue();
        wkid.Should().Be(4269);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public void TryResolveMapServerRequestSrid_InvalidSr_FailsAndAssignsServiceSrid()
    {
        var resolved = MapServerEndpoints.TryResolveMapServerRequestSrid(
            "not-a-sr",
            geometryLatestWkid: 3857,
            geometryWkid: 4326,
            serviceSrid: 4269,
            out var srid,
            out var error);

        resolved.Should().BeFalse();
        error.Should().Be("Invalid sr parameter.");
        srid.Should().Be(4269);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void DescribeExportTilesTransferLimit_NamesRequestedCountAndRefusesPackage()
    {
        var message = MapServerEndpoints.DescribeExportTilesTransferLimit(1200, 64);

        message.Should().Contain("1200");
        message.Should().Contain("64");
        message.Should().Contain("estimateExportTilesSize");
        message.Should().Contain("The package was not created.");
    }

    private static IChangeTracker Tracker(long generation)
    {
        var tracker = Substitute.For<IChangeTracker>();
        tracker.GetCurrentGenerationAsync(Arg.Any<CancellationToken>()).Returns(generation);
        return tracker;
    }

    private static IChangeTracker TrackerThrowing(Exception exception)
    {
        var tracker = Substitute.For<IChangeTracker>();
        tracker.GetCurrentGenerationAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<long>(exception));
        return tracker;
    }

    private static string BuildKey(long generation)
    {
        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Revision = 1, Environment = "test" },
            "etag-stable",
            DateTimeOffset.UnixEpoch);
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "parcels", Name = "Parcels" }
        };

        IReadOnlyList<RasterMapRenderingPipeline.RenderLayerDescriptor> renderLayers = [];
        return MapServerEndpoints.BuildMapServerTileCacheKey(
            generation,
            storageOptions: null,
            snapshot,
            serviceId: "parcels",
            service,
            renderLayers,
            serviceSrid: 3857,
            maxFeatures: 1000,
            rlsFingerprint: "rls",
            fieldMaskFingerprint: "mask",
            z: 4,
            y: 5,
            x: 6);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

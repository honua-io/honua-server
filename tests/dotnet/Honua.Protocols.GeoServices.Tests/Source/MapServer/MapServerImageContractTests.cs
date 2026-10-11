// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
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
    public async Task BuildMapServerTileCacheKey_GenerationChange_ChangesKey()
    {
        var unchanged = await BuildKeyAsync(changeTracker: null);
        var generationZero = await BuildKeyAsync(changeTracker: Tracker(0));
        var edited = await BuildKeyAsync(changeTracker: Tracker(2));

        generationZero.Should().Be(unchanged);
        edited.Should().NotBe(unchanged);
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

    private static Task<string> BuildKeyAsync(IChangeTracker? changeTracker)
    {
        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Revision = 1, Environment = "test" },
            "etag-stable",
            DateTimeOffset.UnixEpoch);
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "parcels", Name = "Parcels" }
        };

        IReadOnlyList<RenderLayerDescriptor> renderLayers = Array.Empty<RenderLayerDescriptor>();
        return MapServerEndpoints.BuildMapServerTileCacheKeyAsync(
            changeTracker,
            CancellationToken.None,
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
}

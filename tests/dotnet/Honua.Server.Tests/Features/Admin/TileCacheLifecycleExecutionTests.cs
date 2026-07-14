// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Unit coverage for the bounded generated tile-cache expire/delete lifecycle operations (#2661):
/// the target window must remove exactly the in-bound tracked keys (storage-first for delete), leave
/// out-of-bound keys untouched, leave a failed delete tracked for a retry, and resume a partially
/// completed generation from the live index without redoing completed work.
/// </summary>
public sealed class TileCacheLifecycleExecutionTests
{
    private const string InBoundKey = "prefix/imageserver/tiles/1/webmercatorquad/default/abc123/2/1/1.png";
    private const string OtherLayerKey = "prefix/imageserver/tiles/2/webmercatorquad/default/abc123/2/1/1.png";
    private const string OtherZoomKey = "prefix/imageserver/tiles/1/webmercatorquad/default/abc123/9/1/1.png";
    private const string OtherStyleKey = "prefix/imageserver/tiles/1/webmercatorquad/night/abc123/2/1/1.png";

    [UnitTest]
    public async Task Delete_BoundedWindow_RemovesInBoundKeysAndLeavesOutOfBound()
    {
        var index = new StatefulKeyIndex();
        index.Seed(InBoundKey, 100);
        index.Seed(OtherLayerKey, 100);
        index.Seed(OtherZoomKey, 100);
        index.Seed(OtherStyleKey, 100);

        var storage = Substitute.For<ICloudFileStorage>();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad",
                Style = "default",
                MinZoom = 0,
                MaxZoom = 5
            },
            index,
            storage);

        result.Status.Should().Be(OperationStatus.Completed);
        result.SuccessfulTiles.Should().Be(1);
        index.Removed.Should().BeEquivalentTo([InBoundKey]);
        index.Remaining.Should().BeEquivalentTo([OtherLayerKey, OtherZoomKey, OtherStyleKey]);
        await storage.Received(1).DeleteAsync(InBoundKey, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Delete_WhenStorageDeleteFails_LeavesKeyTracked()
    {
        var index = new StatefulKeyIndex();
        index.Seed(InBoundKey, 100);

        var storage = Substitute.For<ICloudFileStorage>();
        storage.DeleteAsync(InBoundKey, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("boom"));

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad"
            },
            index,
            storage);

        result.Status.Should().Be(OperationStatus.Failed);
        result.FailedTiles.Should().Be(1);
        // Mirrors the eviction sweep: a failed storage delete must not drop the index key.
        index.Removed.Should().BeEmpty();
        index.Remaining.Should().BeEquivalentTo([InBoundKey]);
    }

    [UnitTest]
    public async Task Delete_AfterMidwayFailure_ResumesRemainingKeys()
    {
        const string key1 = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/0.png";
        const string key2 = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/1.png";
        var index = new StatefulKeyIndex();
        index.Seed(key1, 100);
        index.Seed(key2, 100);

        var failSecond = true;
        var storage = Substitute.For<ICloudFileStorage>();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var key = ci.ArgAt<string>(0);
                if (key == key2 && failSecond)
                {
                    throw new InvalidOperationException("transient");
                }

                return Task.FromResult(true);
            });

        var checkpointStore = new InMemoryTileCacheGenerationCheckpointStore();
        var request = new TileOperationStartRequest
        {
            Operation = "delete",
            LayerId = 1,
            TileMatrixSetId = "WebMercatorQuad",
            GenerationId = "gen-resume-1"
        };

        var first = await ExecuteAsync(request, index, storage, checkpointStore);
        first.Status.Should().Be(OperationStatus.Failed);
        index.Removed.Should().BeEquivalentTo([key1]);
        index.Remaining.Should().BeEquivalentTo([key2]);

        // Fix-forward retry under the same generation id: the second key now succeeds.
        failSecond = false;
        var second = await ExecuteAsync(request, index, storage, checkpointStore);

        second.Status.Should().Be(OperationStatus.Completed);
        index.Remaining.Should().BeEmpty();
        (await checkpointStore.LoadAsync("gen-resume-1")).Should().BeNull();
    }

    [UnitTest]
    public async Task Expire_BoundedWindow_DropsIndexWithoutDeletingBytes()
    {
        var index = new StatefulKeyIndex();
        index.Seed(InBoundKey, 100);
        index.Seed(OtherLayerKey, 100);

        var storage = Substitute.For<ICloudFileStorage>();

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "expire",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad"
            },
            index,
            storage);

        result.Status.Should().Be(OperationStatus.Completed);
        result.SuccessfulTiles.Should().Be(1);
        index.Removed.Should().BeEquivalentTo([InBoundKey]);
        // Expire retains the bytes: storage delete is never called.
        await storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Delete_WithBbox_OnlyRemovesTilesInsideExtent()
    {
        const string inside = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/0.png";
        const string outside = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/3/3.png";
        var index = new StatefulKeyIndex();
        index.Seed(inside, 100);
        index.Seed(outside, 100);

        var storage = Substitute.For<ICloudFileStorage>();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad",
                MinZoom = 2,
                MaxZoom = 2,
                // North-west quadrant only: tile (2,0,0) is inside, (2,3,3) is not.
                Bbox = [-179d, 40d, -95d, 84d]
            },
            index,
            storage);

        result.Status.Should().Be(OperationStatus.Completed);
        index.Removed.Should().BeEquivalentTo([inside]);
        index.Remaining.Should().BeEquivalentTo([outside]);
    }

    private static async Task<TileOperationProgress> ExecuteAsync(
        TileOperationStartRequest request,
        ITileCacheKeyIndex keyIndex,
        ICloudFileStorage storage,
        ITileCacheGenerationCheckpointStore? checkpointStore = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IMetadataV2GraphProvider>());
        services.AddSingleton(Substitute.For<ITileProvider>());
        services.AddSingleton(keyIndex);
        services.AddSingleton(storage);
        using var provider = services.BuildServiceProvider();

        var cacheInvalidationService = new OutputCacheInvalidationService(
            cacheStore: null,
            responseCache: null,
            metadataCache: null,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
            refreshCoordinator: null,
            logger: NullLogger<OutputCacheInvalidationService>.Instance);

        var core = new TileOperationExecutionCore(
            Substitute.For<IUniversalProgressStore>(),
            cacheInvalidationService,
            Options.Create(new TileOptions()),
            Options.Create(new LimitsOptions()),
            NullLogger.Instance,
            maxTilesCeiling: 100_000,
            checkpointStore);

        var started = TileOperationProgress.CreateInitial(
            Guid.NewGuid().ToString("N"),
            request.Operation,
            request.ServiceId,
            request.LayerId,
            request.TileMatrixSetId);

        return await core.ExecuteAsync(started, request, provider, CancellationToken.None);
    }

    private sealed class StatefulKeyIndex : ITileCacheKeyIndex
    {
        private readonly ConcurrentDictionary<string, long> _entries = new(StringComparer.Ordinal);

        public ConcurrentBag<string> Removed { get; } = [];

        public bool IsEnabled => true;

        public IReadOnlyList<string> Remaining => [.. _entries.Keys];

        public void Seed(string key, long sizeBytes) => _entries[key] = sizeBytes;

        public Task RecordAccessAsync(string key, long sizeBytes, CancellationToken cancellationToken = default)
        {
            _entries[key] = sizeBytes;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TileCacheEntry>> SnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TileCacheEntry>>(
                [.. _entries.Select(kvp => new TileCacheEntry(kvp.Key, kvp.Value, DateTimeOffset.UtcNow))]);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            if (_entries.TryRemove(key, out _))
            {
                Removed.Add(key);
            }

            return Task.CompletedTask;
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.TileOperations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Resume / partial-failure / cancellation evidence for the generated tile-cache generation
/// checkpoint core (issue #2661). Each scenario drives the shared
/// <see cref="TileOperationExecutionCore"/> seed path with a recording tile source and asserts
/// that a retry under the same generation id regenerates only the failed units and re-renders
/// zero already-successful units.
/// </summary>
public sealed class TileCacheGenerationCheckpointResumeTests
{
    private const string GenerationId = "gen-resume-001";

    [Fact]
    public async Task Seed_WithMidwayFailure_ThenRetry_RegeneratesOnlyFailedUnits()
    {
        var store = new InMemoryTileCacheGenerationCheckpointStore();
        var renderCounts = new ConcurrentDictionary<(int Z, int X, int Y), int>();
        var failingTiles = new HashSet<(int Z, int X, int Y)> { (1, 1, 1) };

        var provider = CreateRecordingProvider(renderCounts, () => failingTiles, onCall: null);
        using var services = CreateServiceProvider(provider);
        var core = CreateCore(store);
        var request = SeedRequest();

        // Attempt 1: one tile fails. The whole grid is attempted; the generation ends Failed and
        // the checkpoint is retained so a retry can fix-forward.
        var first = await core.ExecuteAsync(StartedProgress(), request, services, CancellationToken.None);

        first.Status.Should().Be(OperationStatus.Failed);
        renderCounts.Should().HaveCount(4, "every tile in the 2x2 grid is attempted on the first pass");
        renderCounts.Values.Should().OnlyContain(count => count == 1);
        store.Count.Should().Be(1, "a partial failure leaves the checkpoint in place");
        var checkpoint = await store.LoadAsync(GenerationId);
        checkpoint!.FailedUnits.Should().ContainSingle().Which.Should().Be("1/1/1/1");

        // Attempt 2 (same generation id): the failing tile now succeeds.
        failingTiles.Clear();
        var second = await core.ExecuteAsync(StartedProgress(), request, services, CancellationToken.None);

        second.Status.Should().Be(OperationStatus.Completed);
        renderCounts[(1, 1, 1)].Should().Be(2, "only the previously failed unit is regenerated on retry");
        renderCounts[(1, 0, 0)].Should().Be(1, "already-successful units are never re-rendered");
        renderCounts[(1, 1, 0)].Should().Be(1);
        renderCounts[(1, 0, 1)].Should().Be(1);
        store.Count.Should().Be(0, "a clean generation deletes its checkpoint");
    }

    [Fact]
    public async Task Seed_WhenCancelledMidRun_LeavesResumableCheckpoint()
    {
        var store = new InMemoryTileCacheGenerationCheckpointStore();
        var renderCounts = new ConcurrentDictionary<(int Z, int X, int Y), int>();
        using var cts = new CancellationTokenSource();

        // Cancel once the second tile has rendered so at least one metatile block has been
        // checkpointed before cancellation is observed.
        var callCount = 0;
        var provider = CreateRecordingProvider(
            renderCounts,
            () => new HashSet<(int, int, int)>(),
            onCall: () =>
            {
                if (Interlocked.Increment(ref callCount) == 2)
                {
                    cts.Cancel();
                }
            });

        using var services = CreateServiceProvider(provider);
        var core = CreateCore(store);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => core.ExecuteAsync(StartedProgress(), SeedRequest(), services, cts.Token));

        store.Count.Should().Be(1, "a cancelled generation leaves its checkpoint for resume");
        var checkpoint = await store.LoadAsync(GenerationId);
        checkpoint!.CompletedMetatileBlocks.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Seed_WhenFullyClean_DeletesCheckpoint()
    {
        var store = new InMemoryTileCacheGenerationCheckpointStore();
        var renderCounts = new ConcurrentDictionary<(int Z, int X, int Y), int>();
        var provider = CreateRecordingProvider(renderCounts, () => new HashSet<(int, int, int)>(), onCall: null);
        using var services = CreateServiceProvider(provider);
        var core = CreateCore(store);

        var result = await core.ExecuteAsync(StartedProgress(), SeedRequest(), services, CancellationToken.None);

        result.Status.Should().Be(OperationStatus.Completed);
        store.Count.Should().Be(0);
    }

    [Fact]
    public async Task Seed_WithoutGenerationId_DoesNotCheckpoint()
    {
        var store = new InMemoryTileCacheGenerationCheckpointStore();
        var renderCounts = new ConcurrentDictionary<(int Z, int X, int Y), int>();
        var provider = CreateRecordingProvider(renderCounts, () => new HashSet<(int, int, int)>(), onCall: null);
        using var services = CreateServiceProvider(provider);
        var core = CreateCore(store);

        var request = SeedRequest() with { GenerationId = null };
        var result = await core.ExecuteAsync(StartedProgress(), request, services, CancellationToken.None);

        result.Status.Should().Be(OperationStatus.Completed);
        store.Count.Should().Be(0, "without a generation id the seed never touches the checkpoint store");
    }

    private static TileOperationStartRequest SeedRequest() => new()
    {
        Operation = "seed",
        LayerId = 1,
        TileMatrixSetId = "WebMercatorQuad",
        MinZoom = 1,
        MaxZoom = 1,
        GenerationId = GenerationId
    };

    private static TileOperationProgress StartedProgress() =>
        TileOperationProgress.CreateInitial("job-" + GenerationId, "seed", null, 1, "WebMercatorQuad") with
        {
            Status = OperationStatus.Processing
        };

    private static TileOperationExecutionCore CreateCore(ITileCacheGenerationCheckpointStore store)
    {
        var cacheInvalidationService = new OutputCacheInvalidationService(
            cacheStore: null,
            responseCache: null,
            metadataCache: null,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            refreshCoordinator: null,
            logger: NullLogger<OutputCacheInvalidationService>.Instance);

        return new TileOperationExecutionCore(
            new InMemoryProgressStore(),
            cacheInvalidationService,
            Options.Create(new TileOptions()),
            Options.Create(new LimitsOptions()),
            NullLogger.Instance,
            maxTilesCeiling: 5_000,
            checkpointStore: store);
    }

    private static ITileProvider CreateRecordingProvider(
        ConcurrentDictionary<(int Z, int X, int Y), int> renderCounts,
        Func<HashSet<(int, int, int)>> failingTiles,
        Action? onCall)
    {
        var provider = Substitute.For<ITileProvider>();
        provider.GetMvtTileAsync(
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<FeatureQuery?>(),
                Arg.Any<TileOptions>(),
                Arg.Any<TileLimits>(),
                Arg.Any<GridGeometry?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var x = ci.ArgAt<int>(1);
                var y = ci.ArgAt<int>(2);
                var z = ci.ArgAt<int>(3);
                renderCounts.AddOrUpdate((z, x, y), 1, static (_, count) => count + 1);
                onCall?.Invoke();
                if (failingTiles().Contains((z, x, y)))
                {
                    throw new InvalidOperationException($"render failed for {z}/{x}/{y}");
                }

                return Task.FromResult<byte[]?>([1, 2, 3]);
            });

        return provider;
    }

    private static ServiceProvider CreateServiceProvider(ITileProvider provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IMetadataV2GraphProvider>());
        services.AddSingleton(provider);
        return services.BuildServiceProvider();
    }

    private sealed class InMemoryProgressStore : IUniversalProgressStore
    {
        private readonly ConcurrentDictionary<string, IOperationProgress> _entries = new(StringComparer.Ordinal);

        public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _entries[operationId] = progress;
            return Task.CompletedTask;
        }

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(string operationId, IOperationProgress progress, OperationStatus expectedStatus, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(ProgressCompareAndSetResult.Updated);

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult(_entries.TryGetValue(operationId, out var p) && p is TProgress typed ? typed : null);

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _entries.TryGetValue(operationId, out var p);
            return Task.FromResult(p);
        }

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _entries.TryRemove(operationId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([.. _entries.Keys]);

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<IReadOnlyList<TProgress>>([.. _entries.Values.OfType<TProgress>()]);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Metadata.Caching;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Metadata.Caching;

/// <summary>
/// Unit tests for the shared per-instance Metadata v2 graph snapshot cache and its caching
/// provider decorator (MCP A2 hot-path caching). Proves repeated reads reuse one materialized
/// snapshot, staleness is TTL-bounded, catalog writes invalidate immediately, disabling the cache
/// is a transparent pass-through, and failed loads are never cached.
/// </summary>
public sealed class MetadataV2GraphSnapshotCacheTests
{
    private const string Environment = "test-env";

    [Fact]
    public async Task GetCurrentAsync_RepeatedCalls_LoadsBackingStoreOnce()
    {
        var provider = new CountingProvider(SnapshotWithRevision(1));
        var cache = NewCache(ttlSeconds: 60, out _);
        var sut = new CachingMetadataV2GraphProvider(provider, cache, Environment);

        for (var i = 0; i < 10; i++)
        {
            var snapshot = await sut.GetCurrentAsync();
            snapshot.Revision.Should().Be(1);
        }

        provider.GetCurrentCalls.Should().Be(1, "the snapshot should be materialized once and reused within the TTL window");
    }

    [Fact]
    public async Task GetCurrentAsync_AfterTtlExpires_ReloadsBackingStore()
    {
        var provider = new CountingProvider(SnapshotWithRevision(1));
        var cache = NewCache(ttlSeconds: 30, out var clock);
        var sut = new CachingMetadataV2GraphProvider(provider, cache, Environment);

        await sut.GetCurrentAsync();
        await sut.GetCurrentAsync();
        provider.GetCurrentCalls.Should().Be(1);

        // Advance past the TTL so the next read must reload — this is the multi-node staleness bound.
        clock.Advance(TimeSpan.FromSeconds(31));
        provider.Next = SnapshotWithRevision(2);

        var reloaded = await sut.GetCurrentAsync();
        reloaded.Revision.Should().Be(2);
        provider.GetCurrentCalls.Should().Be(2);
    }

    [Fact]
    public async Task Invalidate_DropsCachedSnapshot_SoNextReadReloads()
    {
        var provider = new CountingProvider(SnapshotWithRevision(1));
        var cache = NewCache(ttlSeconds: 3600, out _);
        var sut = new CachingMetadataV2GraphProvider(provider, cache, Environment);

        (await sut.GetCurrentAsync()).Revision.Should().Be(1);
        provider.GetCurrentCalls.Should().Be(1);

        // Simulate a catalog write on this node: SaveAsync invalidates the shared cache.
        provider.Next = SnapshotWithRevision(2);
        cache.Invalidate(Environment);

        (await sut.GetCurrentAsync()).Revision.Should().Be(2, "invalidation must force a fresh read even inside the TTL window");
        provider.GetCurrentCalls.Should().Be(2);
    }

    [Fact]
    public async Task InvalidateAll_DropsEveryEnvironment()
    {
        var provider = new CountingProvider(SnapshotWithRevision(1));
        var cache = NewCache(ttlSeconds: 3600, out _);
        var sut = new CachingMetadataV2GraphProvider(provider, cache, Environment);

        await sut.GetCurrentAsync();
        provider.Next = SnapshotWithRevision(2);
        cache.InvalidateAll();

        (await sut.GetCurrentAsync()).Revision.Should().Be(2);
        provider.GetCurrentCalls.Should().Be(2);
    }

    [Fact]
    public async Task GetCurrentAsync_WhenCacheDisabled_AlwaysReadsBackingStore()
    {
        var provider = new CountingProvider(SnapshotWithRevision(1));
        var cache = NewCache(ttlSeconds: 60, out _, enabled: false);
        var sut = new CachingMetadataV2GraphProvider(provider, cache, Environment);

        await sut.GetCurrentAsync();
        await sut.GetCurrentAsync();
        await sut.GetCurrentAsync();

        provider.GetCurrentCalls.Should().Be(3, "a disabled cache must be a transparent pass-through");
    }

    [Fact]
    public async Task GetCurrentAsync_WhenTtlIsZero_AlwaysReadsBackingStore()
    {
        var provider = new CountingProvider(SnapshotWithRevision(1));
        var cache = NewCache(ttlSeconds: 0, out _);
        var sut = new CachingMetadataV2GraphProvider(provider, cache, Environment);

        await sut.GetCurrentAsync();
        await sut.GetCurrentAsync();

        provider.GetCurrentCalls.Should().Be(2);
    }

    [Fact]
    public async Task GetCurrentAsync_ConcurrentMisses_CoalesceIntoSingleLoad()
    {
        var provider = new CountingProvider(SnapshotWithRevision(1)) { LoadDelay = TimeSpan.FromMilliseconds(50) };
        var cache = NewCache(ttlSeconds: 60, out _);
        var sut = new CachingMetadataV2GraphProvider(provider, cache, Environment);

        var tasks = Enumerable.Range(0, 32).Select(_ => sut.GetCurrentAsync().AsTask()).ToArray();
        await Task.WhenAll(tasks);

        tasks.Should().OnlyContain(t => t.Result.Revision == 1);
        provider.GetCurrentCalls.Should().Be(1, "a stampede of concurrent misses must trigger a single backing-store read");
    }

    [Fact]
    public async Task GetCurrentAsync_WhenLoadThrows_DoesNotCacheFailure()
    {
        var provider = new CountingProvider(SnapshotWithRevision(1)) { ThrowOnce = true };
        var cache = NewCache(ttlSeconds: 60, out _);
        var sut = new CachingMetadataV2GraphProvider(provider, cache, Environment);

        var act = async () => await sut.GetCurrentAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();

        // The failed load must not have been cached — the retry succeeds and loads afresh.
        var snapshot = await sut.GetCurrentAsync();
        snapshot.Revision.Should().Be(1);
        provider.GetCurrentCalls.Should().Be(2);
    }

    private static MetadataV2GraphSnapshotCache NewCache(int ttlSeconds, out TestClock clock, bool enabled = true)
    {
        clock = new TestClock();
        var options = Options.Create(new CacheOptions
        {
            MetadataGraphCacheEnabled = enabled,
            MetadataGraphTtlSeconds = ttlSeconds,
        });
        return new MetadataV2GraphSnapshotCache(options, clock);
    }

    private static MetadataV2GraphSnapshot SnapshotWithRevision(long revision)
    {
        var graph = new MetadataV2Graph
        {
            Environment = Environment,
            Revision = revision,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
        return new MetadataV2GraphSnapshot(graph, $"\"rev-{revision}\"", DateTimeOffset.UtcNow);
    }

    private sealed class CountingProvider(MetadataV2GraphSnapshot initial)
        : Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider
    {
        private int _calls;

        public int GetCurrentCalls => Volatile.Read(ref _calls);

        public MetadataV2GraphSnapshot Next { get; set; } = initial;

        public TimeSpan LoadDelay { get; set; }

        public bool ThrowOnce { get; set; }

        public async ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (LoadDelay > TimeSpan.Zero)
            {
                await Task.Delay(LoadDelay, cancellationToken).ConfigureAwait(false);
            }

            if (ThrowOnce)
            {
                ThrowOnce = false;
                throw new InvalidOperationException("transient load failure");
            }

            return Next;
        }

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => new((MetadataV2GraphSnapshot?)null);
    }

    /// <summary>Manually advanced monotonic clock for deterministic TTL tests.</summary>
    private sealed class TestClock : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, (long)(delta.TotalSeconds * TimestampFrequency));
    }
}

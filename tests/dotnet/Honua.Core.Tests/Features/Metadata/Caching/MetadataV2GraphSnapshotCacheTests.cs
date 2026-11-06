// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Metadata.Caching;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task SRV_INF_013_CancellingLeadingCaller_DoesNotCancelSharedLoad()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = NewCache(ttlSeconds: 60, out _);
        using var leadingCancellation = new CancellationTokenSource();

        async ValueTask<MetadataV2GraphSnapshot> Load(CancellationToken cancellationToken)
        {
            cancellationToken.Should().Be(CancellationToken.None);
            loadStarted.TrySetResult();
            await releaseLoad.Task;
            return SnapshotWithRevision(1);
        }

        var leading = cache.GetOrLoadAsync(Environment, Load, leadingCancellation.Token).AsTask();
        await loadStarted.Task;
        var waiting = cache.GetOrLoadAsync(Environment, Load, CancellationToken.None).AsTask();
        leadingCancellation.Cancel();
        releaseLoad.TrySetResult();

        Func<Task> awaitLeading = async () => await leading;
        await awaitLeading.Should().ThrowAsync<OperationCanceledException>();
        (await waiting).Revision.Should().Be(1);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task SRV_INF_013_SharedLoad_RunsInOwnScope_SurvivingInitiatingRequestScope()
    {
        var gate = new LoadGate();
        var services = new ServiceCollection();
        services.AddSingleton(gate);
        services.AddScoped<ScopedLoader>();
        await using var root = services.BuildServiceProvider();
        var cache = NewCache(ttlSeconds: 60, out _);
        using var leadingCancellation = new CancellationTokenSource();

        var requestScope = root.CreateAsyncScope();
        var requestLoader = requestScope.ServiceProvider.GetRequiredService<ScopedLoader>();
        var leadingProvider = new CachingMetadataV2GraphProvider(
            requestLoader,
            cache,
            Environment,
            root.GetRequiredService<IServiceScopeFactory>(),
            static scope => scope.GetRequiredService<ScopedLoader>());

        var leading = leadingProvider.GetCurrentAsync(leadingCancellation.Token).AsTask();
        await gate.Started.Task;
        var waiting = cache.GetOrLoadAsync(Environment, _ => throw new InvalidOperationException("must coalesce"));

        // The initiating request gives up and its scope is torn down while the load is in flight.
        leadingCancellation.Cancel();
        Func<Task> awaitLeading = async () => await leading;
        await awaitLeading.Should().ThrowAsync<OperationCanceledException>();
        await requestScope.DisposeAsync();
        requestLoader.Disposed.Should().BeTrue();

        gate.Release.TrySetResult();

        (await waiting).Revision.Should().Be(1, "the surviving load must not depend on the disposed request scope");
        requestLoader.Calls.Should().Be(0, "the shared load must not run on the initiating request's scoped services");
        gate.LoaderDisposedDuringLoad.Should().BeFalse();
        gate.LoaderDisposedAfterLoad.Task.IsCompleted.Should().BeTrue("the load-owned scope is disposed once the load finishes");
    }

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task GetCurrentAsync_WithRevisionProbe_PublishOnInstanceA_ReadOnInstanceBSeesItWithinTtl()
    {
        // One database, two Lambda instances, each with its own in-process cache and no broadcast.
        var database = new CountingProvider(SnapshotWithRevision(1));
        var probe = new DatabaseEtagProbe(database);
        var cacheA = NewCache(ttlSeconds: 3600, out _);
        var cacheB = NewCache(ttlSeconds: 3600, out _);

        (await new CachingMetadataV2GraphProvider(database, cacheB, Environment, probe).GetCurrentAsync())
            .Revision.Should().Be(1);

        // Instance A publishes: the database moves to revision 2 and only A's cache is invalidated.
        database.Next = SnapshotWithRevision(2);
        cacheA.Invalidate(Environment);

        // The next request on instance B (a new scope) must see the new revision, not 404 for the TTL.
        var requestOnB = new CachingMetadataV2GraphProvider(database, cacheB, Environment, probe);
        (await requestOnB.GetCurrentAsync()).Revision.Should()
            .Be(2, "a publish on another instance must be visible on the next request without Redis (#6762)");
        database.GetCurrentCalls.Should().Be(2, "the full catalog reloads only when the revision changed");

        // Later requests reuse the reloaded snapshot: one probe each, no further loads.
        (await new CachingMetadataV2GraphProvider(database, cacheB, Environment, probe).GetCurrentAsync())
            .Revision.Should().Be(2);
        database.GetCurrentCalls.Should().Be(2);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task GetCurrentAsync_WithRevisionProbe_ProbesOncePerScope()
    {
        var database = new CountingProvider(SnapshotWithRevision(1));
        var probe = new DatabaseEtagProbe(database);
        var cache = NewCache(ttlSeconds: 3600, out _);
        var scope = new CachingMetadataV2GraphProvider(database, cache, Environment, probe);

        for (var i = 0; i < 5; i++)
        {
            (await scope.GetCurrentAsync()).Revision.Should().Be(1);
        }

        probe.Calls.Should().Be(1, "repeat reads inside one request reuse the validated snapshot");
        database.GetCurrentCalls.Should().Be(1);

        // A same-scope write invalidates the cache; the next read gets a different snapshot and is
        // revalidated rather than served from the memo.
        database.Next = SnapshotWithRevision(2);
        cache.Invalidate(Environment);
        (await scope.GetCurrentAsync()).Revision.Should().Be(2);
        probe.Calls.Should().Be(2);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task GetCurrentAsync_WithRevisionProbe_NoActivatedRevision_ServesCachedSnapshot()
    {
        var database = new CountingProvider(SnapshotWithRevision(1));
        var probe = new DatabaseEtagProbe(database) { ReturnNull = true };
        var cache = NewCache(ttlSeconds: 3600, out _);

        await new CachingMetadataV2GraphProvider(database, cache, Environment, probe).GetCurrentAsync();
        await new CachingMetadataV2GraphProvider(database, cache, Environment, probe).GetCurrentAsync();

        database.GetCurrentCalls.Should().Be(1, "a compat/empty snapshot has no pointer row to compare, so the TTL governs");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task GetCurrentAsync_WithRevisionProbe_CacheDisabled_DoesNotProbe()
    {
        var database = new CountingProvider(SnapshotWithRevision(1));
        var probe = new DatabaseEtagProbe(database);
        var cache = NewCache(ttlSeconds: 60, out _, enabled: false);

        await new CachingMetadataV2GraphProvider(database, cache, Environment, probe).GetCurrentAsync();

        probe.Calls.Should().Be(0, "every read already hits the store when caching is off");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task InvalidateIfCurrent_KeepsNewerSnapshot()
    {
        var database = new CountingProvider(SnapshotWithRevision(2));
        var cache = NewCache(ttlSeconds: 3600, out _);
        await cache.GetOrLoadAsync(Environment, database.GetCurrentAsync);

        cache.InvalidateIfCurrent(Environment, "\"rev-1\"");
        await cache.GetOrLoadAsync(Environment, database.GetCurrentAsync);
        database.GetCurrentCalls.Should().Be(1, "a stale-etag drop must not evict a snapshot that is already newer");

        cache.InvalidateIfCurrent(Environment, "\"rev-2\"");
        await cache.GetOrLoadAsync(Environment, database.GetCurrentAsync);
        database.GetCurrentCalls.Should().Be(2);
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

    private sealed class DatabaseEtagProbe(CountingProvider database)
        : Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphRevisionProbe
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool ReturnNull { get; set; }

        public ValueTask<string?> GetCurrentEtagAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return new(ReturnNull ? null : database.Next.Etag);
        }
    }

    private sealed class LoadGate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LoaderDisposedAfterLoad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool LoaderDisposedDuringLoad { get; set; }
    }

    private sealed class ScopedLoader(LoadGate gate)
        : Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider, IDisposable
    {
        private int _calls;
        private bool _loading;

        public int Calls => Volatile.Read(ref _calls);

        public bool Disposed { get; private set; }

        public async ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            _loading = true;
            gate.Started.TrySetResult();
            await gate.Release.Task.ConfigureAwait(false);
            if (Disposed)
            {
                gate.LoaderDisposedDuringLoad = true;
                throw new ObjectDisposedException(nameof(ScopedLoader));
            }

            _loading = false;
            return SnapshotWithRevision(1);
        }

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => new((MetadataV2GraphSnapshot?)null);

        public void Dispose()
        {
            Disposed = true;
            if (_loading)
            {
                gate.LoaderDisposedDuringLoad = true;
            }
            else if (Calls > 0)
            {
                gate.LoaderDisposedAfterLoad.TrySetResult();
            }
        }
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

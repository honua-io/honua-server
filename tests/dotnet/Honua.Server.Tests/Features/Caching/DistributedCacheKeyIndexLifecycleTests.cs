// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(ProtocolNames.TestQuality)]
public sealed class DistributedCacheKeyIndexLifecycleTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task Renewal_DelayedOlderTracking_DoesNotShortenNewerDeadline()
    {
        var clock = new IndexClock();
        var backend = new ExpiringCache(clock);
        await using var index = new DistributedCacheKeyIndex(backend, NullLogger.Instance, clock);
        await backend.SetAsync("key", [1], Options(TimeSpan.FromSeconds(10)));
        await backend.SetAsync("key", [2], Options(TimeSpan.FromMinutes(1)));
        await index.TrackAsync("index", "key", TimeSpan.FromMinutes(1), default);
        await index.TrackAsync("index", "key", TimeSpan.FromSeconds(10), default);

        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(["key"], await index.ReadAsync("index", default));
        Assert.Equal(new byte[] { 2 }, await backend.GetAsync("key"));
        await index.RemoveAsync("index", ["key"], default);
        Assert.Empty(await index.ReadAsync("index", default));
        // Index maintenance never deletes a payload itself.
        Assert.NotNull(await backend.GetAsync("key"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task IdleMaintenance_ReclaimsTwoConcreteScopesWithoutAmbientContext()
    {
        var clock = new IndexClock();
        var backend = new ExpiringCache(clock);
        var ambient = new AsyncLocal<object?> { Value = new object() };
        await using var index = new DistributedCacheKeyIndex(backend, NullLogger.Instance, clock);
        foreach (var scope in new[] { "scope:default:", "scope:schema:tenant-a:" })
        {
            await backend.SetAsync(scope + "value", [1], Options(TimeSpan.FromSeconds(1)));
            await index.TrackAsync(scope + "index", scope + "value", TimeSpan.FromSeconds(1), default);
        }
        ambient.Value = null;
        backend.BeforeGet = (_, _) =>
        {
            Assert.Null(ambient.Value);
            return Task.CompletedTask;
        };
        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !backend.Contains("scope:default:index") && !backend.Contains("scope:schema:tenant-a:index"));
        await index.DisposeAsync();
        Assert.Equal(0, clock.ActiveTimers);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task IdleMaintenance_PreservesAnchorAndRemovesExpiredNeighborWithoutForegroundAccess()
    {
        var clock = new IndexClock();
        var backend = new ExpiringCache(clock);
        await using var index = new DistributedCacheKeyIndex(backend, NullLogger.Instance, clock);
        await backend.SetAsync("anchor", [1], Options(TimeSpan.FromMinutes(1)));
        await index.TrackAsync("index", "anchor", TimeSpan.FromMinutes(1), default);
        await backend.SetAsync("short", [2], Options(TimeSpan.FromSeconds(1)));
        await index.TrackAsync("index", "short", TimeSpan.FromSeconds(1), default);

        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() =>
        {
            using var document = JsonDocument.Parse(backend.Get("index")!);
            return document.RootElement.GetProperty("keys").GetArrayLength() == 1;
        });
        using var remaining = JsonDocument.Parse(backend.Get("index")!);
        Assert.Equal("anchor", remaining.RootElement.GetProperty("keys")[0].GetString());
        Assert.NotNull(backend.Get("anchor"));
        Assert.Null(backend.Get("short"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task LegacyIndex_BoundedProbesPreserveLiveKeysThenReclaimExpiredMembership()
    {
        var clock = new IndexClock();
        var backend = new ExpiringCache(clock);
        var keys = Enumerable.Range(0, 100).Select(value => "expired:" + value).Append("live").ToArray();
        await backend.SetAsync("index", JsonSerializer.SerializeToUtf8Bytes(new { keys }), Options(TimeSpan.FromDays(30)));
        await backend.SetAsync("live", [1], Options(TimeSpan.FromHours(1)));
        await using var index = new DistributedCacheKeyIndex(backend, NullLogger.Instance, clock);
        Assert.Contains("live", await index.ReadAsync("index", default));
        backend.Reads.Clear();
        for (var tick = 0; tick < 5; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => backend.Reads.Contains("index"));
            // Taking the gate waits for the maintenance pass before checking its result.
            var remaining = await index.ReadAsync("index", default);
            Assert.Contains("live", remaining);
            Assert.InRange(backend.Reads.Count(key => key != "index"), 1, 36);
            backend.Reads.Clear();
        }
        Assert.Equal(["live"], await index.ReadAsync("index", default));
        await backend.RemoveAsync("live");
        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !backend.Contains("index"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task Maintenance_BoundsVisitedScopesAndPerTickBackendWork()
    {
        var clock = new IndexClock();
        var backend = new ExpiringCache(clock);
        await using var index = new DistributedCacheKeyIndex(backend, NullLogger.Instance, clock);
        for (var scope = 0; scope < 1025; scope++)
        {
            await index.TrackAsync("index:" + scope, "value:" + scope, TimeSpan.FromMinutes(10), default);
        }
        backend.Reads.Clear();
        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => backend.Reads.Count >= 4);
        await index.DisposeAsync();
        Assert.Equal(new[] { "index:1", "index:2", "index:3", "index:4" }, backend.Reads.ToArray());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task Dispose_DrainsActiveAndQueuedForegroundCallsAndRejectsNewCalls()
    {
        var clock = new IndexClock();
        var backend = new ExpiringCache(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeGet = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var index = new DistributedCacheKeyIndex(backend, NullLogger.Instance, clock);
        var first = index.TrackAsync("index", "first", TimeSpan.FromMinutes(1), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = index.TrackAsync("index", "second", TimeSpan.FromMinutes(1), default);
        var disposal = index.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposal.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => index.ReadAsync("index", default));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, queued, disposal).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, clock.ActiveTimers);
        await index.DisposeAsync();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task CanceledGateWaiter_DoesNotPreventDisposalAfterActiveCallCompletes()
    {
        var clock = new IndexClock();
        var backend = new ExpiringCache(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeGet = async (_, _) => { entered.TrySetResult(); await release.Task; };
        var index = new DistributedCacheKeyIndex(backend, NullLogger.Instance, clock);
        var first = index.ReadAsync("index", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var waiting = index.ReadAsync("index", cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var disposal = index.DisposeAsync().AsTask();
        release.TrySetResult();
        await Task.WhenAll(first, disposal).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task Dispose_WaitsForIssuedMaintenanceRead()
    {
        var clock = new IndexClock();
        var backend = new ExpiringCache(clock);
        var index = new DistributedCacheKeyIndex(backend, NullLogger.Instance, clock);
        await index.TrackAsync("index", "value", TimeSpan.FromMinutes(1), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeGet = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        clock.Advance(TimeSpan.FromSeconds(5));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = index.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, clock.ActiveTimers);
    }

    [UnitTheory]
    [InlineData("{}")]
    [InlineData("{\"keys\":null}")]
    [Operation(Operations.Cache)]
    public async Task LegacyDocument_MissingKeys_RemainsEmpty(string json)
    {
        var clock = new IndexClock();
        var backend = new ExpiringCache(clock);
        await backend.SetAsync("index", System.Text.Encoding.UTF8.GetBytes(json), Options(TimeSpan.FromDays(30)));
        await using var index = new DistributedCacheKeyIndex(backend, NullLogger.Instance, clock);
        Assert.Empty(await index.ReadAsync("index", default));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task MaintenanceStartup_DoesNotRetainInitialRequestObject()
    {
        var (index, request) = CreateWithRequestContext();
        await using var owner = index;
        for (var attempt = 0; attempt < 3 && request.IsAlive; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        Assert.False(request.IsAlive);
        GC.KeepAlive(index);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DistributedCacheKeyIndex, WeakReference) CreateWithRequestContext()
    {
        var ambient = new AsyncLocal<object?> { Value = new object() };
        var weak = new WeakReference(ambient.Value);
        try
        {
            return (new DistributedCacheKeyIndex(new ExpiringCache(TimeProvider.System), NullLogger.Instance, TimeProvider.System), weak);
        }
        finally
        {
            ambient.Value = null;
        }
    }

    private static DistributedCacheEntryOptions Options(TimeSpan ttl) => new() { AbsoluteExpirationRelativeToNow = ttl };

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class IndexClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
        private IndexTimer? _timer;
        public int ActiveTimers => _timer is { Disposed: false } ? 1 : 0;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.True(ExecutionContext.IsFlowSuppressed());
            return _timer = new IndexTimer(callback, state);
        }
        public void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref _ticks, duration.Ticks);
            _timer?.Fire();
        }
        private sealed class IndexTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public void Fire() { if (!Disposed) { callback(state); } }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ExpiringCache(TimeProvider clock) : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, (byte[] Value, long Deadline)> _entries = new(StringComparer.Ordinal);
        public ConcurrentQueue<string> Reads { get; } = new();
        public Func<string, CancellationToken, Task>? BeforeGet { get; set; }
        public bool Contains(string key) => _entries.ContainsKey(key);
        public byte[]? Get(string key)
        {
            if (!_entries.TryGetValue(key, out var entry)) { return null; }
            if (entry.Deadline > clock.GetUtcNow().UtcTicks) { return entry.Value; }
            _entries.TryRemove(key, out _);
            return null;
        }
        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            Reads.Enqueue(key);
            if (BeforeGet is not null) { await BeforeGet(key, token); }
            return Get(key);
        }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => _entries[key] = (value, clock.GetUtcNow().Add(options.AbsoluteExpirationRelativeToNow!.Value).UtcTicks);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
        public void Remove(string key) => _entries.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }
}

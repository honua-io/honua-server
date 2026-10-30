// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Models;
using Honua.Migration;
using Honua.Infrastructure.Progress;
using Honua.Infrastructure.Redis;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Redis;

/// <summary>
/// A thawed Lambda sandbox tears down its Redis connections under the first commands it sends,
/// which fail with the completed-reader pipe fault (honua-server#6753). These cases simulate that
/// fault on the shared post-idle probe and on the progress-store read paths.
/// </summary>
[Collection("Unit")]
public sealed class RedisThawRecoveryTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);

    [UnitTest]
    public async Task ProbeIfIdleAsync_AfterIdleGap_RetriesCompletedReaderOnEveryConnection()
    {
        var clock = new SplitClock();
        var infrastructure = new FaultingProbe(faults: 1);
        var cache = new FaultingProbe(faults: 1);
        var probe = CreateProbe(clock, infrastructure, cache);

        clock.Advance(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3));
        var ran = await probe.ProbeIfIdleAsync(CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(2, infrastructure.Calls);
        Assert.Equal(2, cache.Calls);
    }

    [UnitTest]
    public async Task ProbeIfIdleAsync_WithinThreshold_DoesNotProbe()
    {
        var clock = new SplitClock();
        var target = new FaultingProbe(faults: 0);
        var probe = CreateProbe(clock, target);

        clock.Advance(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var ran = await probe.ProbeIfIdleAsync(CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(0, target.Calls);
    }

    [UnitTest]
    public async Task ProbeIfIdleAsync_WallClockGapOnly_StillProbes()
    {
        // A paused microVM may not advance the monotonic clock across a freeze.
        var clock = new SplitClock();
        var target = new FaultingProbe(faults: 0);
        var probe = CreateProbe(clock, target);

        clock.Advance(monotonic: TimeSpan.Zero, wallClock: TimeSpan.FromMinutes(4));
        var ran = await probe.ProbeIfIdleAsync(CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(1, target.Calls);
    }

    [UnitTest]
    public async Task ProbeIfIdleAsync_ConnectionStillDown_DoesNotThrow()
    {
        var clock = new SplitClock();
        var target = new FaultingProbe(faults: int.MaxValue);
        var probe = CreateProbe(clock, target);

        clock.Advance(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        var ran = await probe.ProbeIfIdleAsync(CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(2, target.Calls);
    }

    [UnitTest]
    public async Task Middleware_FirstRequestAfterFreeze_ProbesBeforeThePipelineAndNotOnTheNextRequest()
    {
        var clock = new SplitClock();
        var target = new FaultingProbe(faults: 1);
        var probe = CreateProbe(clock, target);
        var probeCallsSeenByPipeline = new List<int>();
        var middleware = new RedisThawProbeMiddleware(
            _ =>
            {
                probeCallsSeenByPipeline.Add(target.Calls);
                return Task.CompletedTask;
            },
            probe);

        clock.Advance(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3));
        await middleware.InvokeAsync(new DefaultHttpContext());
        clock.Advance(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        await middleware.InvokeAsync(new DefaultHttpContext());

        Assert.Equal([2, 2], probeCallsSeenByPipeline);
    }

    [UnitTest]
    public async Task UniversalProgressStore_GetProgressAsync_CompletedReaderOnce_ReadsAndStaysOnRedis()
    {
        var cache = new CompletedReaderOnceCache();
        var store = new UniversalProgressStore(cache, NullLogger<UniversalProgressStore>.Instance);
        var progress = ExportProgress.CreateInitial("export-1", "csv", "svc", 1, 10);
        await store.SetProgressAsync("export-1", progress, TimeSpan.FromMinutes(5));

        cache.FaultNextRead();
        var loaded = await store.GetProgressAsync<ExportProgress>("export-1");

        Assert.NotNull(loaded);
        Assert.False(store.IsUsingFallback);
        Assert.Equal(0, cache.PendingFaults);
    }

    [UnitTest]
    public async Task UniversalProgressStore_GetProgressAsync_CompletedReaderTwice_MapsTo503()
    {
        var cache = new CompletedReaderOnceCache();
        var store = new UniversalProgressStore(cache, NullLogger<UniversalProgressStore>.Instance);

        cache.FaultNextRead(times: 2);
        var exception = await Assert.ThrowsAsync<DistributedProgressStateUnavailableException>(
            () => store.GetProgressAsync("job-1"));

        Assert.True(RedisClientFault.ContainsCompletedReader(exception));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ExceptionMapper.Map(exception).StatusCode);
    }

    [UnitTest]
    public void ExceptionMapper_DistributedProgressStateUnavailable_Is503()
    {
        var exception = new DistributedProgressStateUnavailableException("get progress", new TimeoutException());

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ExceptionMapper.Map(exception).StatusCode);
    }

    [UnitTest]
    public void ExceptionMapper_WrappedCompletedReader_Is503_OtherWrappedInvalidOperationStays500()
    {
        var wrapped = new InvalidOperationException(
            "store read failed",
            new InvalidOperationException(RedisClientFault.CompletedReaderMessage));
        var unrelated = new InvalidOperationException("store read failed", new InvalidOperationException("bug"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ExceptionMapper.Map(wrapped).StatusCode);
        Assert.Equal(StatusCodes.Status500InternalServerError, ExceptionMapper.Map(unrelated).StatusCode);
    }

    private static RedisThawProbe CreateProbe(TimeProvider clock, params FaultingProbe[] targets)
        => new(
            targets.Select((target, index) => new RedisThawProbe.ProbeTarget($"target-{index}", target.RunAsync)).ToList(),
            clock,
            NullLogger.Instance,
            Threshold);

    private sealed class FaultingProbe(int faults)
    {
        private int _remainingFaults = faults;

        public int Calls { get; private set; }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (_remainingFaults > 0)
            {
                _remainingFaults--;
                throw new InvalidOperationException(RedisClientFault.CompletedReaderMessage);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>A memory cache whose next reads fail like a torn StackExchange.Redis pipe.</summary>
    private sealed class CompletedReaderOnceCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));

        public int PendingFaults { get; private set; }

        public void FaultNextRead(int times = 1) => PendingFaults = times;

        public byte[]? Get(string key) => ThrowIfFaulted() ?? _inner.Get(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            ThrowIfFaulted();
            return _inner.GetAsync(key, token);
        }

        public void Refresh(string key) => _inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);

        public void Remove(string key) => _inner.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => _inner.SetAsync(key, value, options, token);

        private byte[]? ThrowIfFaulted()
        {
            if (PendingFaults > 0)
            {
                PendingFaults--;
                throw new InvalidOperationException(RedisClientFault.CompletedReaderMessage);
            }

            return null;
        }
    }

    /// <summary>A clock whose monotonic and wall-clock readings advance independently.</summary>
    private sealed class SplitClock : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = new(2026, 10, 30, 4, 35, 35, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan monotonic, TimeSpan wallClock)
        {
            _timestamp += monotonic.Ticks;
            _utcNow += wallClock;
        }
    }
}

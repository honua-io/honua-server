// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Infrastructure.Progress;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Runtime.CompilerServices;

namespace Honua.Server.Tests.Features.Admin;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Cache)]
public sealed class TileCacheExpiryHostedServiceTests
{
    [UnitTest]
    public async Task SweepAsync_WhenEnabled_QueuesInvalidateJobForEachTarget()
    {
        var jobService = new RecordingTileOperationJobService();
        var options = Options.Create(new TileCacheExpiryOptions
        {
            Enabled = true,
            IntervalSeconds = 3600,
            Targets =
            [
                new TileCacheExpiryTarget
                {
                    ServiceId = "parks",
                    LayerId = 3,
                    TileMatrixSetId = "WebMercatorQuad"
                },
                new TileCacheExpiryTarget
                {
                    ServiceId = "roads"
                }
            ]
        });
        var service = new TileCacheExpiryHostedService(
            jobService,
            options,
            NullLogger<TileCacheExpiryHostedService>.Instance);

        await service.SweepAsync(CancellationToken.None);

        jobService.Requests.Should().HaveCount(2);
        jobService.Requests.Should().OnlyContain(request => request.Operation == "invalidate");
        jobService.Requests[0].ServiceId.Should().Be("parks");
        jobService.Requests[0].LayerId.Should().Be(3);
        jobService.Requests[1].ServiceId.Should().Be("roads");
    }

    [UnitTest]
    public async Task SweepAsync_SkipsTargetWithoutServiceOrLayer()
    {
        var jobService = new RecordingTileOperationJobService();
        var options = Options.Create(new TileCacheExpiryOptions
        {
            Enabled = true,
            Targets =
            [
                new TileCacheExpiryTarget { TileMatrixSetId = "WebMercatorQuad" },
                new TileCacheExpiryTarget { LayerId = 7 }
            ]
        });
        var service = new TileCacheExpiryHostedService(
            jobService,
            options,
            NullLogger<TileCacheExpiryHostedService>.Instance);

        await service.SweepAsync(CancellationToken.None);

        jobService.Requests.Should().ContainSingle();
        jobService.Requests[0].LayerId.Should().Be(7);
    }

    [UnitTest]
    public async Task ExecuteAsync_WhenDisabled_QueuesNothing()
    {
        var jobService = new RecordingTileOperationJobService();
        var service = new TileCacheExpiryHostedService(
            jobService,
            Options.Create(new TileCacheExpiryOptions()),
            NullLogger<TileCacheExpiryHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await service.StopAsync(CancellationToken.None);

        jobService.Requests.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ExecuteAsync_WhenSweepThrows_RetriesOnNextTick()
    {
        var jobService = new ThrowingTileOperationJobService();
        var clock = new ManualTimeProvider();
        var service = new TileCacheExpiryHostedService(
            jobService,
            Options.Create(new TileCacheExpiryOptions
            {
                Enabled = true,
                IntervalSeconds = TileCacheExpiryHostedService.MinIntervalSeconds,
                Targets = [new TileCacheExpiryTarget { LayerId = 1 }]
            }),
            NullLogger<TileCacheExpiryHostedService>.Instance,
            clock);

        await service.StartAsync(CancellationToken.None);
        // BackgroundService.StartAsync queues ExecuteAsync, so the sweep timer does not exist yet.
        await WaitUntilAsync(() => clock.HasTimer);
        clock.HasTimer.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(TileCacheExpiryHostedService.MinIntervalSeconds));
        await WaitUntilAsync(() => jobService.Calls >= 1);
        clock.Advance(TimeSpan.FromSeconds(TileCacheExpiryHostedService.MinIntervalSeconds));
        await WaitUntilAsync(() => jobService.Calls >= 2);

        service.ExecuteTask.Should().NotBeNull();
        service.ExecuteTask!.IsFaulted.Should().BeFalse();
        jobService.Calls.Should().BeGreaterThanOrEqualTo(2);

        await service.StopAsync(CancellationToken.None);
        service.ExecuteTask.Should().NotBeNull();
        service.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ThrowingTileOperationJobService : RecordingTileOperationJobService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public override Task<string> StartAsync(
            TileOperationStartRequest request,
            string? schemaName = null,
            string? tenantScope = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                throw new RedisTimeoutException("Timeout awaiting response, command=GET", CommandStatus.Sent);
            }

            return base.StartAsync(request, schemaName, tenantScope, cancellationToken);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _utcTicks = DateTimeOffset.UnixEpoch.UtcTicks;
        private ManualTimer? _timer;

        public bool HasTimer => _timer is { Disposed: false };

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _timer = new ManualTimer(callback, state, dueTime, period);

        public void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref _utcTicks, duration.Ticks);
            _timer?.Advance(duration);
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
        {
            private TimeSpan _remaining = dueTime;
            private TimeSpan _period = period;

            public bool Disposed { get; private set; }

            public void Advance(TimeSpan duration)
            {
                if (Disposed)
                {
                    return;
                }

                _remaining -= duration;
                if (_remaining > TimeSpan.Zero)
                {
                    return;
                }

                callback(state);
                _remaining += _period > TimeSpan.Zero ? _period : TimeSpan.Zero;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Disposed)
                {
                    return false;
                }

                _remaining = dueTime;
                _period = period;
                return true;
            }

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private class RecordingTileOperationJobService : ITileOperationJobService
    {
        public List<TileOperationStartRequest> Requests { get; } = [];

        public virtual Task<string> StartAsync(
            TileOperationStartRequest request,
            string? schemaName = null,
            string? tenantScope = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult($"job-{Requests.Count}");
        }

        public Task<TileOperationProgress?> GetAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.FromResult<TileOperationProgress?>(null);

        public Task<IReadOnlyList<TileOperationProgress>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TileOperationProgress>>([]);

        public Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<string?> RetryAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public async IAsyncEnumerable<string> ReadQueuedJobIdsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task ProcessQueuedJobAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

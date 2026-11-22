// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Events.Outbox;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Events.Outbox;

// An idle outbox dispatcher backs off instead of polling every IdlePollIntervalMs, and a local outbox
// write wakes it.
[Protocol(TestProtocols.TestQuality)]
public sealed class OutboxDispatcherIdlePacingTests
{
    private static readonly TimeSpan _observationTimeout = TimeSpan.FromSeconds(15);

    [UnitTest]
    public async Task IdleDispatcher_BacksOffFromTheBusyIntervalToTheCap()
    {
        var claims = new ClaimCounter();
        using var dispatcher = NewDispatcher(claims, new Pacing(100, 800, new PollWakeSignal<IFeatureChangeOutboxRepository>()));

        await dispatcher.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(3));
        await dispatcher.StopAsync(CancellationToken.None);

        // A fixed 100 ms poll makes about 30 passes in 3 s; 100+200+400+800+800 ms waits make about 6.
        claims.Count.Should().BeInRange(3, 10);
    }

    [UnitTest]
    public async Task IdleDispatcher_WokenByALocalOutboxWrite_ClaimsBeforeTheIdleCap()
    {
        var claims = new ClaimCounter();
        var wake = new PollWakeSignal<IFeatureChangeOutboxRepository>();
        using var dispatcher = NewDispatcher(claims, new Pacing(1_000, 60_000, wake));

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            await claims.WaitForAsync(3).WaitAsync(_observationTimeout);
            var before = claims.Count;
            wake.Wake();

            await claims.WaitForAsync(before + 1).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task FailingClaims_KeepTheBusyInterval()
    {
        var claims = new ClaimCounter { Fail = true };
        using var dispatcher = NewDispatcher(claims, new Pacing(100, 10_000, new PollWakeSignal<IFeatureChangeOutboxRepository>()));

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            // Nine passes take about 1 s at the 100 ms busy interval; backing off toward 10 s would need about 25 s.
            await claims.WaitForAsync(9).WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task EqualIdleAndMaxIntervals_KeepsFixedPollingInterval()
    {
        var claims = new ClaimCounter();
        using var dispatcher = NewDispatcher(claims, new Pacing(100, 100, new PollWakeSignal<IFeatureChangeOutboxRepository>()));

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            // Setting MaxIdlePollIntervalMs == IdlePollIntervalMs disables backoff; nine passes take about 1 s.
            await claims.WaitForAsync(9).WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    private sealed record Pacing(int IdleMs, int MaxIdleMs, PollWakeSignal<IFeatureChangeOutboxRepository> Wake);

    private static OutboxDispatcherBackgroundService NewDispatcher(ClaimCounter claims, Pacing pacing)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFeatureChangeOutboxRepository>(claims.Repository);
        services.AddSingleton(pacing.Wake);
        services.AddSingleton(Substitute.For<IFeatureChangeEventPublisher>());
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);
        var options = Options.Create(new OutboxDispatcherOptions
        {
            IdlePollIntervalMs = pacing.IdleMs,
            MaxIdlePollIntervalMs = pacing.MaxIdleMs,
            RecoveryIntervalSeconds = 3_600,
        });
        return new OutboxDispatcherBackgroundService(
            services.BuildServiceProvider(), capability, options, TestTelemetry.CreateOutboxMetrics(),
            NullLogger<OutboxDispatcherBackgroundService>.Instance);
    }

    private sealed class ClaimCounter
    {
        private readonly Lock _gate = new();
        private readonly List<(int Count, TaskCompletionSource Done)> _waiters = [];
        private int _count;

        public ClaimCounter()
        {
            Repository = Substitute.For<IFeatureChangeOutboxRepository>();
            Repository.ClaimPendingAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(_ => Record());
            Repository.GetBacklogMetricsAsync(Arg.Any<CancellationToken>())
                .Returns(new OutboxBacklogMetrics { PendingCount = 0, DeadLetteredCount = 0, OldestPendingAgeSeconds = 0 });
        }

        public IFeatureChangeOutboxRepository Repository { get; }

        public bool Fail { get; init; }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _count;
                }
            }
        }

        public Task WaitForAsync(int count)
        {
            lock (_gate)
            {
                if (_count >= count)
                {
                    return Task.CompletedTask;
                }

                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, done));
                return done.Task;
            }
        }

        private Task<IReadOnlyList<FeatureChangeOutboxEntry>> Record()
        {
            lock (_gate)
            {
                _count++;
                foreach (var waiter in _waiters.Where(w => w.Count <= _count))
                {
                    waiter.Done.TrySetResult();
                }
            }

            return Fail
                ? Task.FromException<IReadOnlyList<FeatureChangeOutboxEntry>>(new InvalidOperationException("claim failed"))
                : Task.FromResult<IReadOnlyList<FeatureChangeOutboxEntry>>([]);
        }
    }
}

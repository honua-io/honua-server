// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Server.Features.Realtime;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Realtime;

[Collection("Unit")]
public sealed class RealtimeStreamLeaseTests
{
    [UnitTest]
    public async Task RenewFailure_TerminatesOwnershipAndCancelsPendingReads()
    {
        var clock = new LeaseClock();
        var store = new RenewalStore(_ => Task.FromResult(false));
        await using var lease = await RealtimeStreamLease.TryAcquireAsync(store, "session", CancellationToken.None,
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20), clock);
        lease.Should().NotBeNull();
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lease!.LostToken.Register(() => lost.TrySetResult());

        clock.Advance(TimeSpan.FromMilliseconds(20));
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lease.IsHeld.Should().BeFalse();
        store.Acquisitions.Should().Be(1, "loss must never reacquire an ownership epoch");
        await lease.DisposeAsync();
        store.Releases.Should().Be(1);
    }

    [UnitTest]
    public async Task PendingRenewal_LocalDeadlineEndsDeliveryBeforeStoreReply()
    {
        var clock = new LeaseClock();
        var renewal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RenewalStore(_ => renewal.Task);
        await using var lease = await RealtimeStreamLease.TryAcquireAsync(store, "session", CancellationToken.None,
            TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(20), clock);
        lease.Should().NotBeNull();
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lease!.LostToken.Register(() => lost.TrySetResult());
        try
        {
            clock.Advance(TimeSpan.FromMilliseconds(20));
            await store.Renewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lease.IsHeld.Should().BeTrue("the renewal is pending before the local deadline");
            clock.Advance(TimeSpan.FromMilliseconds(280));
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lease.IsHeld.Should().BeFalse();
        }
        finally
        {
            renewal.TrySetResult(true);
        }

        await lease.DisposeAsync();
        lease.IsHeld.Should().BeFalse("a late successful renewal cannot revive a lost stream");
    }

    // Timers and monotonic elapsed time advance together, independently of runner scheduling.
    private sealed class LeaseClock : TimeProvider
    {
        private readonly TransitionClock _timers = new();
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _timers.CreateTimer(callback, state, dueTime, period);

        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _timestamp, elapsed.Ticks);
            _timers.Advance(elapsed);
        }
    }

    private sealed class RenewalStore(Func<CancellationToken, Task<bool>> renew) : ILeaseStore
    {
        public TaskCompletionSource Renewed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Acquisitions { get; private set; }
        public int Releases { get; private set; }

        public Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            Acquisitions++;
            return Task.FromResult(true);
        }

        public Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            Renewed.TrySetResult();
            return renew(cancellationToken);
        }

        public Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default)
        {
            Releases++;
            return Task.CompletedTask;
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Coordination;

public sealed class IdlePollPacerTests
{
    private static readonly TimeSpan _longInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _observationTimeout = TimeSpan.FromSeconds(10);

    [UnitTest]
    public void NextIdleDelay_IdlePasses_DoubleFromTheBusyIntervalToTheCeiling()
    {
        var pacer = new IdlePollPacer(new PollWakeSignal<IdlePollPacerTests>());

        var delays = Enumerable.Range(0, 7).Select(_ => pacer.NextIdleDelay().TotalSeconds).ToArray();

        delays.Should().Equal(5, 10, 20, 40, 60, 60, 60);
    }

    [UnitTest]
    public async Task WaitAfterWorkAsync_AfterIdlePasses_ResetsTheBackOff()
    {
        var pacer = new IdlePollPacer(new PollWakeSignal<IdlePollPacerTests>(), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(60));
        pacer.NextIdleDelay();
        pacer.NextIdleDelay();
        pacer.NextIdleDelay();

        await pacer.WaitAfterWorkAsync(CancellationToken.None).WaitAsync(_observationTimeout);

        pacer.NextIdleDelay().Should().Be(TimeSpan.FromMilliseconds(10));
        pacer.NextIdleDelay().Should().Be(TimeSpan.FromMilliseconds(20));
    }

    [UnitTest]
    public void NextIdleDelay_CeilingEqualToTheBusyInterval_NeverBacksOff()
    {
        var interval = TimeSpan.FromSeconds(5);
        var pacer = new IdlePollPacer(new PollWakeSignal<IdlePollPacerTests>(), interval, interval);

        Enumerable.Range(0, 4).Select(_ => pacer.NextIdleDelay()).Should().AllBeEquivalentTo(interval);
    }

    [UnitTest]
    public void NextIdleDelay_NoWakeSignal_KeepsTheBusyInterval()
    {
        var pacer = new IdlePollPacer(wake: null);

        Enumerable.Range(0, 4).Select(_ => pacer.NextIdleDelay()).Should().AllBeEquivalentTo(IdlePollPacer.DefaultBusyInterval);
    }

    [UnitTest]
    public async Task WaitIdleAsync_WokenRightAfterThePassBegan_WaitsOutTheWakeFloor()
    {
        var wake = new PollWakeSignal<IdlePollPacerTests>();
        var pacer = new IdlePollPacer(wake, _longInterval, _longInterval);
        var elapsed = Stopwatch.StartNew();
        pacer.BeginPoll();
        wake.Wake();

        await pacer.WaitIdleAsync(CancellationToken.None).WaitAsync(_observationTimeout);

        elapsed.Elapsed.Should().BeGreaterThanOrEqualTo(IdlePollPacer.WakeFloor - TimeSpan.FromMilliseconds(50));
    }

    [UnitTest]
    public void Constructor_InvalidIntervals_Throw()
    {
        var zeroBusy = () => new IdlePollPacer(wake: null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        var ceilingBelowBusy = () => new IdlePollPacer(wake: null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));

        zeroBusy.Should().Throw<ArgumentOutOfRangeException>();
        ceilingBelowBusy.Should().Throw<ArgumentOutOfRangeException>();
    }

    [UnitTest]
    public async Task WaitIdleAsync_WokenByAWrite_ReturnsBeforeTheInterval()
    {
        var wake = new PollWakeSignal<IdlePollPacerTests>();
        var pacer = new IdlePollPacer(wake, _longInterval, _longInterval);
        pacer.BeginPoll();

        var waiting = pacer.WaitIdleAsync(CancellationToken.None);
        waiting.IsCompleted.Should().BeFalse();
        wake.Wake();

        await waiting.WaitAsync(_observationTimeout);
    }

    [UnitTest]
    public async Task WaitIdleAsync_WakeDuringThePass_IsNotLost()
    {
        var wake = new PollWakeSignal<IdlePollPacerTests>();
        var pacer = new IdlePollPacer(wake, _longInterval, _longInterval);
        pacer.BeginPoll();
        wake.Wake();

        await pacer.WaitIdleAsync(CancellationToken.None).WaitAsync(_observationTimeout);
    }

    [UnitTest]
    public async Task WaitIdleAsync_WakeBeforeThePassBegan_DoesNotEndTheWait()
    {
        var wake = new PollWakeSignal<IdlePollPacerTests>();
        var pacer = new IdlePollPacer(wake, _longInterval, _longInterval);
        wake.Wake();
        pacer.BeginPoll();

        var waiting = pacer.WaitIdleAsync(CancellationToken.None);

        await waiting.Invoking(task => task.WaitAsync(TimeSpan.FromMilliseconds(300))).Should().ThrowAsync<TimeoutException>();
    }

    [UnitTest]
    public async Task WaitAfterWorkAsync_ItsOwnWrites_DoNotShortenTheInterval()
    {
        var busy = TimeSpan.FromMilliseconds(400);
        var wake = new PollWakeSignal<IdlePollPacerTests>();
        var pacer = new IdlePollPacer(wake, busy, _longInterval);
        pacer.BeginPoll();
        wake.Wake();

        var elapsed = Stopwatch.StartNew();
        await pacer.WaitAfterWorkAsync(CancellationToken.None).WaitAsync(_observationTimeout);

        elapsed.Elapsed.Should().BeGreaterThanOrEqualTo(busy - TimeSpan.FromMilliseconds(50));
    }

    [UnitTest]
    public async Task WaitIdleAsync_Woken_ResetsTheBackOff()
    {
        var wake = new PollWakeSignal<IdlePollPacerTests>();
        var pacer = new IdlePollPacer(wake, _longInterval, _longInterval * 8);
        pacer.NextIdleDelay();
        pacer.NextIdleDelay();
        pacer.BeginPoll();
        wake.Wake();

        await pacer.WaitIdleAsync(CancellationToken.None).WaitAsync(_observationTimeout);

        pacer.NextIdleDelay().Should().Be(_longInterval);
    }

    [UnitTest]
    public async Task WaitIdleAsync_Cancelled_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        var pacer = new IdlePollPacer(new PollWakeSignal<IdlePollPacerTests>(), _longInterval, _longInterval);
        pacer.BeginPoll();

        var waiting = pacer.WaitIdleAsync(cancellation.Token);
        await cancellation.CancelAsync();

        await waiting.Invoking(task => task.WaitAsync(_observationTimeout)).Should().ThrowAsync<OperationCanceledException>();
    }

    [UnitTest]
    public async Task Wake_SeveralLoopsWaitingOnOneSignal_AllWake()
    {
        var wake = new PollWakeSignal<IdlePollPacerTests>();
        var waits = Enumerable.Range(0, 3).Select(_ =>
        {
            var pacer = new IdlePollPacer(wake, _longInterval, _longInterval);
            pacer.BeginPoll();
            return pacer.WaitIdleAsync(CancellationToken.None);
        }).ToArray();

        wake.Wake();

        await Task.WhenAll(waits).WaitAsync(_observationTimeout);
    }

    [UnitTest]
    public async Task SignalWaitAsync_NoWake_ReturnsFalseAfterTheTimeout()
    {
        var wake = new PollWakeSignal<IdlePollPacerTests>();

        var woken = await wake.WaitAsync(wake.Version, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        woken.Should().BeFalse();
    }
}

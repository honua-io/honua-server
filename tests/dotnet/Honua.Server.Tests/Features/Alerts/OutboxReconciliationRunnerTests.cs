// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Alerts;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class OutboxReconciliationRunnerTests
{
    [UnitTest]
    public async Task RunAsync_FailedPass_RetriesAfterConfiguredDelay()
    {
        using var cancellation = new CancellationTokenSource();
        var time = Substitute.For<TimeProvider>();
        var timer = Substitute.For<ITimer>();
        TimerCallback? tick = null;
        object? state = null;
        var interval = TimeSpan.FromSeconds(30);
        time.CreateTimer(Arg.Any<TimerCallback>(), Arg.Any<object?>(), interval, Timeout.InfiniteTimeSpan)
            .Returns(call =>
            {
                tick = call.ArgAt<TimerCallback>(0);
                state = call.ArgAt<object?>(1);
                return timer;
            });
        var failure = new InvalidOperationException("Temporary publishing failure");
        var failures = new List<Exception>();
        var passes = 0;
        Task<int> Reconcile(CancellationToken token)
        {
            token.Should().Be(cancellation.Token);
            if (++passes == 1)
            {
                return Task.FromException<int>(failure);
            }

            cancellation.Cancel();
            return Task.FromResult(0);
        }

        var running = new OutboxReconciliationRunner(time, interval)
            .RunAsync(Reconcile, failures.Add, cancellation.Token);
        passes.Should().Be(1);
        failures.Should().ContainSingle().Which.Should().BeSameAs(failure);
        tick.Should().NotBeNull();
        tick!(state);
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        passes.Should().Be(2);
    }

    [UnitTest]
    public async Task RunAsync_CancelledDuringDelay_StopsWithoutLoggingFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var failures = new List<Exception>();
        var passes = 0;
        var running = new OutboxReconciliationRunner(TimeProvider.System, TimeSpan.FromHours(1))
            .RunAsync(_ => Task.FromResult(++passes), failures.Add, cancellation.Token);

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        passes.Should().Be(1);
        failures.Should().BeEmpty();
    }

    [UnitTest]
    public async Task RunAsync_CancelledDuringPass_StopsWithoutLoggingFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var failures = new List<Exception>();
        Task<int> Reconcile(CancellationToken token)
        {
            cancellation.Cancel();
            return Task.FromCanceled<int>(token);
        }

        await new OutboxReconciliationRunner(TimeProvider.System, TimeSpan.FromHours(1))
            .RunAsync(Reconcile, failures.Add, cancellation.Token);

        failures.Should().BeEmpty();
    }

    [UnitTest]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201:Do not raise reserved exception types",
        Justification = "Injects a runtime failure to verify it propagates without retry or logging.")]
    public async Task RunAsync_OutOfMemory_DoesNotRetry()
    {
        var failure = new OutOfMemoryException();
        var failures = new List<Exception>();
        var running = new OutboxReconciliationRunner(TimeProvider.System, TimeSpan.FromHours(1))
            .RunAsync(_ => Task.FromException<int>(failure), failures.Add, CancellationToken.None);

        var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(() => running);

        thrown.Should().BeSameAs(failure);
        failures.Should().BeEmpty();
    }
}

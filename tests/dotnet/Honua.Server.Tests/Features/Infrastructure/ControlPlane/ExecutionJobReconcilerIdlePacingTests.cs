// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// An idle reconciler backs off, and a job write in this process ends its idle wait.
public sealed class ExecutionJobReconcilerIdlePacingTests
{
    private static readonly TimeSpan _observationTimeout = TimeSpan.FromSeconds(10);

    [UnitTest]
    public async Task IdleLoop_WokenByAJobWrite_PollsAgainBeforeTheBusyInterval()
    {
        var wake = new PollWakeSignal<IExecutionJobStore>();
        var polls = new PollRecorder();
        var source = Substitute.For<IReconcilableJobSource>();
        source.ListBackendReconciliationCandidateIdsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => polls.Record(Array.Empty<string>()));
        using var service = NewService(source, wake);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await polls.WaitForAsync(1).WaitAsync(_observationTimeout);
            var sinceWake = Stopwatch.StartNew();
            wake.Wake();

            await polls.WaitForAsync(2).WaitAsync(_observationTimeout);

            sinceWake.Elapsed.Should().BeLessThan(IdlePollPacer.DefaultBusyInterval - TimeSpan.FromSeconds(1));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task BusyLoop_ItsOwnWritesDoNotMakeItPollEarly()
    {
        var wake = new PollWakeSignal<IExecutionJobStore>();
        var polls = new PollRecorder();
        var source = Substitute.For<IReconcilableJobSource>();
        source.ListBackendReconciliationCandidateIdsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => polls.Record(new[] { "op-1" }));
        var reconciler = Substitute.For<IExecutionJobReconciler>();
        reconciler.ReconcileExecutionJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                wake.Wake();
                return Task.CompletedTask;
            });
        using var service = NewService(source, wake, reconciler);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await polls.WaitForAsync(1).WaitAsync(_observationTimeout);

            var second = polls.WaitForAsync(2);

            await second.Invoking(task => task.WaitAsync(TimeSpan.FromSeconds(2))).Should().ThrowAsync<TimeoutException>();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static ExecutionJobReconcilerBackgroundService NewService(
        IReconcilableJobSource source,
        PollWakeSignal<IExecutionJobStore> wake,
        IExecutionJobReconciler? reconciler = null)
        => new(
            Substitute.For<IExecutionJobStore>(),
            reconciler ?? Substitute.For<IExecutionJobReconciler>(),
            NullLogger<ExecutionJobReconcilerBackgroundService>.Instance,
            source,
            wake);

    private sealed class PollRecorder
    {
        private readonly Lock _gate = new();
        private readonly List<TaskCompletionSource> _waiters = [];
        private int _count;

        public Task<IReadOnlyList<string>> Record(IReadOnlyList<string> result)
        {
            lock (_gate)
            {
                _count++;
                foreach (var waiter in _waiters.Where(waiter => !waiter.Task.IsCompleted).ToArray())
                {
                    waiter.TrySetResult();
                }
            }

            return Task.FromResult(result);
        }

        // Completes once at least the given number of polls ran.
        public Task WaitForAsync(int count)
        {
            lock (_gate)
            {
                if (_count >= count)
                {
                    return Task.CompletedTask;
                }
            }

            return WaitLoopAsync(count);
        }

        private async Task WaitLoopAsync(int count)
        {
            while (true)
            {
                TaskCompletionSource next;
                lock (_gate)
                {
                    if (_count >= count)
                    {
                        return;
                    }

                    next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Add(next);
                }

                await next.Task.ConfigureAwait(false);
            }
        }
    }
}

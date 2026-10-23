// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class JobExecutionReadinessTests
{
    [UnitTest]
    public async Task ReadinessRequiresStartedWorkerAndSuccessfulClaimPoll()
    {
        await using var fixture = new WorkerFixture();
        Assert.False(await fixture.ReadyAsync());
        await fixture.Worker.StartAsync(default);
        await fixture.ClaimEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await fixture.ReadyAsync());
        await fixture.Queue.DidNotReceive().GetQueueDepthAsync(Arg.Any<CancellationToken>());

        fixture.CompleteClaim.TrySetResult(null);
        await fixture.WaitForReadyAsync();
        await fixture.Worker.StopAsync(default);
        Assert.False(await fixture.ReadyAsync());
    }

    [UnitTest]
    public async Task ReadinessBindsQueueStoreAndPerKindRuntimeProfile()
    {
        await using var fixture = new WorkerFixture();
        await fixture.StartReadyAsync();
        fixture.Queue.ClearReceivedCalls();

        Assert.False(await fixture.Worker.IsReadyForAsync(Substitute.For<IJobQueue>(), fixture.Store,
            ExecutionJobKind.FileImport, RuntimeProfiles.Managed));
        Assert.False(await fixture.Worker.IsReadyForAsync(fixture.Queue, Substitute.For<IExecutionJobStore>(),
            ExecutionJobKind.FileImport, RuntimeProfiles.Managed));
        Assert.False(await fixture.Worker.IsReadyForAsync(fixture.Queue, fixture.Store,
            ExecutionJobKind.FileImport, RuntimeProfiles.Native));
        Assert.False(await fixture.Worker.IsReadyForAsync(fixture.Queue, fixture.Store,
            ExecutionJobKind.Geoprocessing, RuntimeProfiles.Managed));
        await fixture.Queue.DidNotReceive().GetQueueDepthAsync(Arg.Any<CancellationToken>());
        Assert.True(await fixture.ReadyAsync());
    }

    [UnitTest]
    public async Task AssignedWorkerCannotAdvertisePersistentQueueReadiness()
    {
        await using var fixture = new WorkerFixture();
        await fixture.StartReadyAsync();
        fixture.Queue.ClearReceivedCalls();
        fixture.Worker.RequeueOnShutdown = true;
        Assert.False(await fixture.ReadyAsync());
        await fixture.Queue.DidNotReceive().GetQueueDepthAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task QueueFailureDoesNotReuseEarlierPositiveReadiness()
    {
        await using var fixture = new WorkerFixture();
        await fixture.StartReadyAsync();
        fixture.Queue.GetQueueDepthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<long>(new IOException("Queue unavailable.")));
        Assert.False(await fixture.ReadyAsync());
        fixture.Queue.GetQueueDepthAsync(Arg.Any<CancellationToken>()).Returns(0L);
        Assert.True(await fixture.ReadyAsync());
    }

    [UnitTest]
    public async Task StopDuringQueueProbeInvalidatesSuccessfulReply()
    {
        await using var fixture = new WorkerFixture();
        await fixture.StartReadyAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Queue.GetQueueDepthAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return reply.Task;
        });
        var pending = fixture.ReadyAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Worker.StopAsync(default);
        reply.TrySetResult(0);
        Assert.False(await pending);
    }

    [UnitTest]
    public async Task CancellationInterruptsAnUnresponsiveReadinessProbe()
    {
        await using var fixture = new WorkerFixture();
        await fixture.StartReadyAsync();
        var reply = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Queue.GetQueueDepthAsync(Arg.Any<CancellationToken>()).Returns(reply.Task);
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.ReadyAsync(cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        reply.TrySetResult(0);
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        internal IJobQueue Queue { get; } = Substitute.For<IJobQueue>();
        internal IExecutionJobStore Store { get; } = Substitute.For<IExecutionJobStore>();
        internal TaskCompletionSource ClaimEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string?> CompleteClaim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal JobExecutionService Worker { get; }

        internal WorkerFixture()
        {
            Queue.TryClaimAsync(Arg.Any<string>(), Arg.Any<IReadOnlySet<ExecutionJobKind>>(),
                Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
                {
                    ClaimEntered.TrySetResult();
                    return CompleteClaim.Task.WaitAsync(call.Arg<CancellationToken>());
                });
            Queue.GetQueueDepthAsync(Arg.Any<CancellationToken>()).Returns(0L);
            Worker = new(Queue, Store, [new ImportExecutor(), new NativeProcessExecutor()],
                new ExecutionJobCancellationTokens(), [], null, NullLogger<JobExecutionService>.Instance);
        }

        internal ValueTask<bool> ReadyAsync(CancellationToken cancellationToken = default)
            => Worker.IsReadyForAsync(Queue, Store, ExecutionJobKind.FileImport, RuntimeProfiles.Managed, cancellationToken);

        internal async Task StartReadyAsync()
        {
            await Worker.StartAsync(default);
            await ClaimEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            CompleteClaim.TrySetResult(null);
            await WaitForReadyAsync();
        }

        internal async Task WaitForReadyAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!await ReadyAsync(deadline.Token))
                await Task.Delay(1, deadline.Token);
        }

        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Worker.StopAsync(deadline.Token);
            Worker.Dispose();
        }
    }

    private sealed class ImportExecutor : IJobExecutor
    {
        public ExecutionJobKind Kind => ExecutionJobKind.FileImport;
        public Task<JobExecutionResult> ExecuteAsync(ExecutionJobRecord job, IJobExecutionContext context,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Readiness must not execute a job.");
    }

    private sealed class NativeProcessExecutor : IJobExecutor
    {
        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;
        public IReadOnlySet<string> AcceptedRuntimeProfiles { get; } = new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };
        public Task<JobExecutionResult> ExecuteAsync(ExecutionJobRecord job, IJobExecutionContext context,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Readiness must not execute a job.");
    }
}

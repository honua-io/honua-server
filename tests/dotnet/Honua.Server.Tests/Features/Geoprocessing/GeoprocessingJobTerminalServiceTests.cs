// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class GeoprocessingJobTerminalServiceTests
{
    private readonly IGeoprocessingJobService _jobs = Substitute.For<IGeoprocessingJobService>();
    private readonly ClaimsPrincipal _principal = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "test-user")], "Test"));

    [Fact]
    public async Task WaitForResultAsync_RetryObservesTerminalWinner_ReturnsCanonicalPackage()
    {
        var queued = CreateJob(ExecutionJobStatus.Queued);
        var succeeded = CreateJob(ExecutionJobStatus.Succeeded);
        var package = CreatePackage();
        _jobs.GetJobForTerminalAsync("job-1", _principal, Arg.Any<CancellationToken>())
            .Returns(queued, succeeded);
        _jobs.GetJobResultsForTerminalAsync("job-1", _principal, Arg.Any<CancellationToken>())
            .Returns(package);
        var sut = CreateService((_, _) => Task.CompletedTask);

        var result = await sut.WaitForResultAsync(
            "job-1", _principal, TimeSpan.FromSeconds(1));

        result.Outcome.Should().Be(GeoprocessingTerminalResultOutcome.Succeeded);
        result.Job.Should().BeSameAs(succeeded);
        result.ResultPackage.Should().BeSameAs(package);
        await _jobs.Received(2).GetJobForTerminalAsync("job-1", _principal, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WaitForTerminalAsync_Timeout_ReturnsTypedOutcome()
    {
        _jobs.GetJobForTerminalAsync("job-1", _principal, Arg.Any<CancellationToken>())
            .Returns(CreateJob(ExecutionJobStatus.Running));
        var sut = CreateService(
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            _ => CreateCancelledSource());

        var result = await sut.WaitForTerminalAsync(
            "job-1", _principal, TimeSpan.FromSeconds(1));

        result.Outcome.Should().Be(GeoprocessingTerminalWaitOutcome.Timeout);
        await _jobs.DidNotReceive().GetJobForTerminalAsync(
            Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WaitForTerminalAsync_ClientDisconnect_ReturnsTypedOutcome()
    {
        using var disconnected = new CancellationTokenSource();
        await disconnected.CancelAsync();
        var sut = CreateService((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));

        var result = await sut.WaitForTerminalAsync(
            "job-1", _principal, TimeSpan.FromSeconds(1), disconnected.Token);

        result.Outcome.Should().Be(GeoprocessingTerminalWaitOutcome.ClientDisconnected);
    }

    [Fact]
    public async Task CancelAsync_CompletionWinsCancelRace_ReturnsAlreadyTerminal()
    {
        var running = CreateJob(ExecutionJobStatus.Running);
        var succeeded = CreateJob(ExecutionJobStatus.Succeeded);
        _jobs.GetJobAsync("job-1", _principal, Arg.Any<CancellationToken>())
            .Returns(running, succeeded);
        _jobs.CancelJobAsync("job-1", _principal, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new GeoprocessingPreconditionFailedException(
                "The job completed before cancellation could be applied.")));
        var sut = CreateService((_, _) => Task.CompletedTask);

        var result = await sut.CancelAsync("job-1", _principal, TimeSpan.FromSeconds(1));

        result.Outcome.Should().Be(GeoprocessingCancelOutcome.AlreadyTerminal);
        result.Job.Should().BeSameAs(succeeded);
    }

    [Fact]
    public async Task CancelAsync_UnsupportedBackend_ReturnsTypedOutcome()
    {
        _jobs.GetJobAsync("job-1", _principal, Arg.Any<CancellationToken>())
            .Returns(CreateJob(ExecutionJobStatus.Running));
        _jobs.CancelJobAsync("job-1", _principal, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new GeoprocessingCancellationUnsupportedException(
                "The remote backend does not support cancellation.")));
        var sut = CreateService((_, _) => Task.CompletedTask);

        var result = await sut.CancelAsync("job-1", _principal, TimeSpan.FromSeconds(1));

        result.Outcome.Should().Be(GeoprocessingCancelOutcome.Unsupported);
    }

    [Fact]
    public async Task CancelAsync_RaceRereadReachesDeadline_ReturnsTypedTimeout()
    {
        using var timeoutSource = new CancellationTokenSource();
        var running = CreateJob(ExecutionJobStatus.Running);
        _jobs.GetJobAsync("job-1", _principal, Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(running),
                _ => Task.FromCanceled<ExecutionJobRecord>(timeoutSource.Token));
        _jobs.CancelJobAsync("job-1", _principal, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                timeoutSource.Cancel();
                return Task.FromException(new GeoprocessingPreconditionFailedException(
                    "The job completed before cancellation could be applied."));
            });
        var sut = CreateService(
            (_, _) => Task.CompletedTask,
            _ => timeoutSource);

        var result = await sut.CancelAsync("job-1", _principal, TimeSpan.FromSeconds(1));

        result.Outcome.Should().Be(GeoprocessingCancelOutcome.Timeout);
    }

    [Fact]
    public async Task CancelOrphanedAsync_UsesDomainOwnedCleanupIntent()
    {
        var running = CreateJob(ExecutionJobStatus.Running);
        _jobs.GetJobForTerminalAsync("job-1", _principal, Arg.Any<CancellationToken>())
            .Returns(running);
        _jobs.CancelAbandonedJobAsync("job-1", _principal, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var sut = CreateService((_, _) => Task.CompletedTask);

        var result = await sut.CancelOrphanedAsync(
            "job-1", _principal, TimeSpan.FromSeconds(1));

        result.Outcome.Should().Be(GeoprocessingCancelOutcome.Cancelled);
        await _jobs.Received(1).CancelAbandonedJobAsync(
            "job-1", _principal, Arg.Any<CancellationToken>());
        await _jobs.DidNotReceive().CancelJobAsync(
            Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
        await _jobs.DidNotReceive().GetJobAsync(
            Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchOrphanedCancellation_ReturnsBeforeSlowCleanupCompletes()
    {
        var running = CreateJob(ExecutionJobStatus.Running);
        var cancelled = CreateJob(ExecutionJobStatus.Cancelled);
        var cleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        _jobs.GetJobForTerminalAsync("job-1", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                {
                    return running;
                }

                cleanupCompleted.TrySetResult();
                return cancelled;
            });
        _jobs.CancelAbandonedJobAsync("job-1", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cleanupStarted.TrySetResult();
                return releaseCleanup.Task;
            });
        var sut = CreateService((_, _) => Task.CompletedTask);

        sut.DispatchOrphanedCancellation("job-1", _principal, TimeSpan.FromSeconds(1));

        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cleanupCompleted.Task.IsCompleted.Should().BeFalse(
            "the response path must not await slow orphan cancellation");

        releaseCleanup.TrySetResult();
        await cleanupCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private GeoprocessingJobTerminalService CreateService(
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<TimeSpan, CancellationTokenSource>? timeoutSourceFactory = null)
        => new(
            _jobs,
            TimeProvider.System,
            delay,
            timeoutSourceFactory ?? (timeout => new CancellationTokenSource(timeout)));

    private static CancellationTokenSource CreateCancelledSource()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source;
    }

    private static ExecutionJobRecord CreateJob(ExecutionJobStatus status) => new()
    {
        OperationId = "job-1",
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Audit = new OperationAuditInfo { RequestedBy = "test-user" },
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = LocalBatchComputeBackend.BackendId,
            WorkloadName = "test-workload"
        }
    };

    private static AnalysisResultPackage CreatePackage() => AnalysisResultPackage.CreateCompleted(
        "result-1",
        new ResultSummary { Title = "Complete" },
        [],
        [],
        new ProvenanceRecord { Sources = [], ProcessDefinitions = [] });
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// A worker-loss retry clears the claim fields; each lost attempt must stay visible as a job event.
public sealed partial class JobReconciliationServiceTests
{
    [UnitTest]
    public async Task HeartbeatExpiry_Retry_RecordsLostAttemptEvent()
    {
        var snapshot = CreateExpiredJob(attemptCount: 1, maxAttempts: 3);
        var (written, logged) = await SweepExpiredJobAsync(snapshot, Substitute.For<IExecutionLogStore>());

        written.Status.Should().Be(ExecutionJobStatus.Queued);
        logged.Should().NotBeNull();
        logged!.Message.Should().StartWith("Attempt 1 of 3 lost: the worker stopped heartbeating");
        logged.Level.Should().Be(ExecutionLogLevel.Warning);
        logged.Phase.Should().Be("worker-lost");
        logged.Metadata.Should().Contain("attempt", "1")
            .And.Contain("attempt.max", "3")
            .And.Contain("worker.id", "worker-1")
            .And.Contain("failure.classification", "worker_lost")
            .And.Contain("attempt.claimedAt", snapshot.ClaimedAt!.Value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    [UnitTest]
    public async Task HeartbeatExpiry_FinalFailure_RecordsLastLostAttemptEvent()
    {
        var (written, logged) = await SweepExpiredJobAsync(CreateExpiredJob(attemptCount: 3, maxAttempts: 3), Substitute.For<IExecutionLogStore>());

        written.Status.Should().Be(ExecutionJobStatus.Failed);
        logged.Should().NotBeNull();
        logged!.Message.Should().StartWith("Attempt 3 of 3 lost:");
        logged.Metadata.Should().Contain("attempt", "3").And.Contain("failure.classification", "worker_lost");
    }

    [UnitTest]
    public async Task HeartbeatExpiry_Retry_StillRequeues_WhenLostAttemptEventCannotBeWritten()
    {
        var logStore = Substitute.For<IExecutionLogStore>();
        logStore.AppendAsync(Arg.Any<string>(), Arg.Any<ExecutionLogEntry>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("log store down"));

        var (written, _) = await SweepExpiredJobAsync(CreateExpiredJob(attemptCount: 1, maxAttempts: 3), logStore);

        written.Status.Should().Be(ExecutionJobStatus.Queued);
    }

    private static ExecutionJobRecord CreateExpiredJob(int attemptCount, int maxAttempts)
        => CreateRunningJob(
            attemptCount: attemptCount,
            retryPolicy: new JobRetryPolicy
            {
                MaxAttempts = maxAttempts,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            heartbeatPolicy: new JobHeartbeatPolicy { Interval = TimeSpan.FromSeconds(1), Timeout = TimeSpan.FromSeconds(1) },
            timeoutPolicy: new JobTimeoutPolicy { MaxDuration = TimeSpan.FromHours(24) });

    private static async Task<(ExecutionJobRecord Written, ExecutionLogEntry? Logged)> SweepExpiredJobAsync(
        ExecutionJobRecord snapshot,
        IExecutionLogStore logStore)
    {
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>()).Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>()).Returns(snapshot);

        using var service = new JobReconciliationService(
            jobStore, Substitute.For<IJobQueue>(), Substitute.For<IQueueClaimReconciler>(), new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), logStore, NullLogger<JobReconciliationService>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        var written = ArgumentsOf(jobStore, nameof(IExecutionJobStore.TrySetAsync)).OfType<ExecutionJobRecord>().LastOrDefault();
        var logged = ArgumentsOf(logStore, nameof(IExecutionLogStore.AppendAsync)).OfType<ExecutionLogEntry>().SingleOrDefault();
        written.Should().NotBeNull("the expired heartbeat must transition the job");
        return (written!, logged);
    }

    private static IEnumerable<object?> ArgumentsOf(object substitute, string methodName)
        => substitute.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == methodName)
            .SelectMany(call => call.GetArguments());
}

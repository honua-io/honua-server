// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class JobExecutionServiceTests
{
    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessJob_CancelledBeforeDispatch_RecoversReceiptWithoutExecuting(bool committed)
    {
        var durable = CreateProvisioningJob() with { CancellationRequestedAt = DateTimeOffset.UtcNow };
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(durable.OperationId, Arg.Any<CancellationToken>()).Returns(_ => durable);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { durable = call.Arg<ExecutionJobRecord>(); return true; });
        var executor = Substitute.For<IJobExecutor, IJobCancellationRecoveryExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        var recovery = ConfigureCancellationRecovery(executor, committed);
        using var service = new JobExecutionService(Substitute.For<IJobQueue>(), store, [executor],
            new ExecutionJobCancellationTokens(), [], null, NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, durable.OperationId, durable.ClaimedBy!);

        Assert.Equal(committed ? ExecutionJobStatus.Succeeded : ExecutionJobStatus.Cancelled, durable.Status);
        if (committed)
        {
            Assert.Contains("committed-import-receipt", durable.ArtifactReferences);
        }
        await recovery.Received(1).RecoverCancellationAsync(Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
        await executor.DidNotReceive().ExecuteAsync(Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
    }

    private static IJobCancellationRecoveryExecutor ConfigureCancellationRecovery(IJobExecutor executor, bool committed)
    {
        var recovery = (IJobCancellationRecoveryExecutor)executor;
        recovery.RecoverCancellationAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                Assert.False(call.Arg<CancellationToken>().IsCancellationRequested);
                if (!committed)
                {
                    return null;
                }

                await call.Arg<IJobExecutionContext>().RecordCommittedEffectAsync("committed-import-receipt");
                return JobExecutionResult.Succeeded() with { CompletedWithCommittedEffects = true };
            });
        return recovery;
    }

    [UnitTest]
    public async Task ProcessJob_CancellationRecoveryUnavailable_RequeuesBeyondExecutionRetryBudget()
    {
        var durable = CreateProvisioningJob() with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow,
            AttemptCount = 3,
            RetryPolicy = new JobRetryPolicy { MaxAttempts = 3 }
        };
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(durable.OperationId, Arg.Any<CancellationToken>()).Returns(_ => durable);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { durable = call.Arg<ExecutionJobRecord>(); return true; });
        var executor = Substitute.For<IJobExecutor, IJobCancellationRecoveryExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        ((IJobCancellationRecoveryExecutor)executor).RecoverCancellationAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<JobExecutionResult?>(new IOException("Receipt store unavailable")));
        var queue = Substitute.For<IJobQueue>();
        using var service = new JobExecutionService(queue, store, [executor],
            new ExecutionJobCancellationTokens(), [], null, NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, durable.OperationId, durable.ClaimedBy!);

        Assert.Equal(ExecutionJobStatus.Queued, durable.Status);
        Assert.NotNull(durable.CancellationRequestedAt);
        Assert.Null(durable.ClaimedBy);
        Assert.NotNull(durable.NextRetryAt);
        await queue.Received(1).RequeueAsync(durable.OperationId, durable.Priority,
            TimeSpan.FromSeconds(5), Arg.Any<CancellationToken>());
        await executor.DidNotReceive().ExecuteAsync(Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
    }
}

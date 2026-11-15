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
    [InlineData(ExecutionJobStatus.Succeeded)]
    [InlineData(ExecutionJobStatus.Failed)]
    public async Task ProcessJob_RecoveryBeyondRetryBudget_NeverStartsAnotherImport(ExecutionJobStatus expectedStatus)
    {
        var durable = CreateProvisioningJob() with
        {
            AttemptCount = 4,
            RetryPolicy = new JobRetryPolicy { MaxAttempts = 3 }
        };
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(durable.OperationId, Arg.Any<CancellationToken>()).Returns(_ => durable);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { durable = call.Arg<ExecutionJobRecord>(); return true; });
        var executor = Substitute.For<IJobExecutor, IJobOutcomeRecoveryExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        var recovery = ConfigureOutcomeRecovery(executor, expectedStatus == ExecutionJobStatus.Succeeded
            ? JobExecutionResult.Succeeded() with { CompletedWithCommittedEffects = true } : null);
        using var service = new JobExecutionService(Substitute.For<IJobQueue>(), store, [executor],
            new ExecutionJobCancellationTokens(), [], null, NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, durable.OperationId, durable.ClaimedBy!);

        Assert.Equal(expectedStatus, durable.Status);
        await recovery.Received(1).RecoverOutcomeAsync(Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
        await executor.DidNotReceive().ExecuteAsync(Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ProcessJob_LastAttemptThrows_RecoversReceiptBeforeTerminalFailure()
    {
        var durable = CreateProvisioningJob() with
        {
            AttemptCount = 3,
            RetryPolicy = new JobRetryPolicy { MaxAttempts = 3 }
        };
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(durable.OperationId, Arg.Any<CancellationToken>()).Returns(_ => durable);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { durable = call.Arg<ExecutionJobRecord>(); return true; });
        var executor = Substitute.For<IJobExecutor, IJobOutcomeRecoveryExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        var recovery = ConfigureOutcomeRecovery(executor,
            JobExecutionResult.Succeeded() with { CompletedWithCommittedEffects = true }, skipFirstLookup: true);
        executor.ExecuteAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<JobExecutionResult>(new IOException("Connection lost")));
        using var service = new JobExecutionService(Substitute.For<IJobQueue>(), store, [executor],
            new ExecutionJobCancellationTokens(), [], null, NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, durable.OperationId, durable.ClaimedBy!);

        Assert.Equal(ExecutionJobStatus.Succeeded, durable.Status);
        await recovery.Received(2).RecoverOutcomeAsync(Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
        await executor.Received(1).ExecuteAsync(Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
    }
}

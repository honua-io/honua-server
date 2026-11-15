// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
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
    [InlineData("Job execution timed out.")]
    [InlineData("Worker drain deadline expired.")]
    public async Task Termination_ReceiptUnavailableThenAbsent_NeverRestartsImport(string reason)
    {
        var durable = CreateProvisioningJob() with { RetryPolicy = new JobRetryPolicy { MaxAttempts = 3 } };
        var workerId = durable.ClaimedBy!;
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(durable.OperationId, Arg.Any<CancellationToken>()).Returns(_ => durable);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { durable = call.Arg<ExecutionJobRecord>(); return true; });
        var executor = Substitute.For<IJobExecutor, IJobOutcomeRecoveryExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        var recovery = (IJobOutcomeRecoveryExecutor)executor;
        var lookupCount = 0;
        recovery.RecoverOutcomeAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++lookupCount == 1
                ? Task.FromException<JobExecutionResult?>(new IOException("Receipt store unavailable"))
                : Task.FromResult<JobExecutionResult?>(null));
        using var service = new JobExecutionService(Substitute.For<IJobQueue>(), store, [executor],
            new ExecutionJobCancellationTokens(), [], null, NullLogger<JobExecutionService>.Instance);
        var terminate = typeof(JobExecutionService).GetMethod("TerminateJobAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)terminate.Invoke(service,
            [durable.OperationId, workerId, ExecutionJobStatus.Failed, reason, CancellationToken.None, true])!;

        Assert.Equal(ExecutionJobStatus.Queued, durable.Status);
        Assert.Equal(1, durable.RetryPolicy!.MaxAttempts);
        durable = durable with { Status = ExecutionJobStatus.Provisioning, ClaimedBy = workerId, AttemptCount = 2 };
        await InvokeProcessJobAsync(service, durable.OperationId, workerId);

        Assert.Equal(ExecutionJobStatus.Failed, durable.Status);
        Assert.Equal(2, lookupCount);
        await executor.DidNotReceive().ExecuteAsync(Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
    }
}

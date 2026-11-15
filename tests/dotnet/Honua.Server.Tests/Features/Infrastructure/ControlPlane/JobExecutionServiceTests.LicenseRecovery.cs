// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Licensing.Abstractions;
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
    public async Task ProcessJob_LicenseExpiresWithLostCommitReply_RecoversBeforeAnotherExecution(ExecutionJobStatus expectedStatus)
    {
        var durable = CreateProvisioningJob() with { AttemptCount = 1, RetryPolicy = new JobRetryPolicy { MaxAttempts = 2 } };
        var workerId = durable.ClaimedBy!;
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(durable.OperationId, Arg.Any<CancellationToken>()).Returns(_ => durable);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { durable = call.Arg<ExecutionJobRecord>(); return true; });
        using var expiry = new CancellationTokenSource();
        var policy = Substitute.For<ILicenseOperationPolicy>();
        policy.OperationCancellation.Returns(expiry.Token);
        var executor = CreateLicenseExpiryExecutor(expiry);
        var recovery = ConfigureOutcomeRecovery(executor, expectedStatus == ExecutionJobStatus.Succeeded
            ? JobExecutionResult.Succeeded() with { CompletedWithCommittedEffects = true } : null);
        using var service = new JobExecutionService(Substitute.For<IJobQueue>(), store, [executor],
            new ExecutionJobCancellationTokens(), [], null, NullLogger<JobExecutionService>.Instance, policy);

        await InvokeProcessJobAsync(service, durable.OperationId, workerId);
        Assert.Equal(ExecutionJobStatus.Queued, durable.Status);
        durable = durable with { Status = ExecutionJobStatus.Provisioning, ClaimedBy = workerId, AttemptCount = 2, CurrentPhase = "Claimed" };
        await InvokeProcessJobAsync(service, durable.OperationId, workerId);
        Assert.Equal(ExecutionJobStatus.Queued, durable.Status);
        await recovery.DidNotReceive().RecoverOutcomeAsync(Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
        policy.OperationCancellation.Returns(CancellationToken.None);
        durable = durable with { Status = ExecutionJobStatus.Provisioning, ClaimedBy = workerId, AttemptCount = 3, CurrentPhase = "Claimed" };
        await InvokeProcessJobAsync(service, durable.OperationId, workerId);

        Assert.Equal(expectedStatus, durable.Status);
        Assert.Equal(expectedStatus == ExecutionJobStatus.Succeeded, durable.CommittedEffectReferences.Contains("committed-import-receipt"));
        await recovery.Received(1).RecoverOutcomeAsync(Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
        await executor.Received(1).ExecuteAsync(Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
    }

    private static IJobExecutor CreateLicenseExpiryExecutor(CancellationTokenSource expiry)
    {
        var executor = Substitute.For<IJobExecutor, IJobOutcomeRecoveryExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                expiry.Cancel();
                return Task.FromException<JobExecutionResult>(new IOException("COMMIT acknowledgement lost"));
            });
        return executor;
    }
}

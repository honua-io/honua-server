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
    [InlineData(1)]
    [InlineData(2)]
    public async Task ProcessJob_LicenseExpiresDuringSuccessWrite_ScrubsKnownOutcome(int attemptCount)
    {
        var durable = CreateProvisioningJob() with { AttemptCount = attemptCount };
        using var expiry = new CancellationTokenSource();
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(durable.OperationId, Arg.Any<CancellationToken>()).Returns(_ => durable);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                durable = call.Arg<ExecutionJobRecord>();
                if (durable.Status == ExecutionJobStatus.Succeeded)
                {
                    expiry.Cancel();
                }
                return true;
            });
        var policy = Substitute.For<ILicenseOperationPolicy>();
        policy.OperationCancellation.Returns(expiry.Token);
        var executor = CreateSuccessfulRecoveryExecutor();
        var recovery = ConfigureOutcomeRecovery(executor,
            JobExecutionResult.Succeeded() with { CompletedWithCommittedEffects = true });
        using var service = new JobExecutionService(Substitute.For<IJobQueue>(), store, [executor],
            new ExecutionJobCancellationTokens(), [], null, NullLogger<JobExecutionService>.Instance, policy);

        await InvokeProcessJobAsync(service, durable.OperationId, durable.ClaimedBy!);

        Assert.Equal(ExecutionJobStatus.Failed, durable.Status);
        Assert.Equal("license expired", durable.ErrorMessage);
        Assert.Empty(durable.ArtifactReferences);
        Assert.Empty(durable.CommittedEffectReferences);
        Assert.Null(durable.PercentComplete);
        await recovery.Received(attemptCount > 1 ? 1 : 0).RecoverOutcomeAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
        await executor.Received(attemptCount > 1 ? 0 : 1).ExecuteAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
    }

    private static IJobExecutor CreateSuccessfulRecoveryExecutor()
    {
        var executor = Substitute.For<IJobExecutor, IJobOutcomeRecoveryExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.Arg<IJobExecutionContext>().PublishArtifactAsync("committed-import-receipt", call.Arg<CancellationToken>());
                await call.Arg<IJobExecutionContext>().RecordCommittedEffectAsync("committed-import-receipt");
                return JobExecutionResult.Succeeded() with { CompletedWithCommittedEffects = true };
            });
        return executor;
    }
}

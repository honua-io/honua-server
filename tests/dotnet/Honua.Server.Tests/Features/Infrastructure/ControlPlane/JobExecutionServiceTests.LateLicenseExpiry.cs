// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Migration;
using Honua.Server.Tests.Import;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class JobExecutionServiceTests
{
    [UnitTheory]
    [InlineData(1, GeoservicesImportStatus.Completed)]
    [InlineData(2, GeoservicesImportStatus.Completed)]
    [InlineData(1, GeoservicesImportStatus.NeedsReview)]
    [InlineData(2, GeoservicesImportStatus.NeedsReview)]
    public async Task ProcessJob_LicenseExpiresDuringSuccessWrite_ScrubsKnownOutcome(
        int attemptCount, GeoservicesImportStatus status)
    {
        using var expiry = new CancellationTokenSource();
        var (store, job) = CreateLateExpiryStore(attemptCount, expiry);
        var policy = Substitute.For<ILicenseOperationPolicy>();
        policy.OperationCancellation.Returns(expiry.Token);
        var progressStore = new ImportExecutionTestProgressStore(store);
        var completed = GeoservicesImportProgress.CreateInitial(job.OperationId,
            "https://example.com/FeatureServer", 0, "imported_data") with
        {
            Status = status,
            DataCommitted = true,
            FeaturesProcessed = 12
        };
        var manager = new GeoservicesImportJobManager(progressStore,
            NullLogger<GeoservicesImportJobManager>.Instance, store, Substitute.For<IJobQueue>());
        var executor = CreateSuccessfulRecoveryExecutor();
        var recovery = ConfigureOutcomeRecovery(executor,
            JobExecutionResult.Succeeded() with { CompletedWithCommittedEffects = true });
        // Both direct and recovered execution expose terminal progress before the canonical CAS.
        await progressStore.SetProgressAsync(job.OperationId, completed);
        using var service = new JobExecutionService(Substitute.For<IJobQueue>(), store, [executor],
            new ExecutionJobCancellationTokens(), [], null, NullLogger<JobExecutionService>.Instance, policy);

        await InvokeProcessJobAsync(service, job.OperationId, job.ClaimedBy!);

        AssertLateExpiryOutcome((await store.GetAsync(job.OperationId))!);
        var polled = await manager.ProgressStore.GetProgressAsync(job.OperationId);
        Assert.NotNull(polled);
        Assert.Equal(GeoservicesImportStatus.Failed, polled.Status);
        Assert.Equal("license expired", polled.ErrorMessage);
        Assert.True(polled.DataCommitted);
        Assert.Equal(12, polled.FeaturesProcessed);
        await recovery.Received(attemptCount > 1 ? 1 : 0).RecoverOutcomeAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
        await executor.Received(attemptCount > 1 ? 0 : 1).ExecuteAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>());
    }

    private static (IExecutionJobStore Store, ExecutionJobRecord Job) CreateLateExpiryStore(
        int attemptCount, CancellationTokenSource expiry)
    {
        var provisioning = CreateProvisioningJob();
        var durable = provisioning with
        {
            AttemptCount = attemptCount,
            Spec = provisioning.Spec with { Kind = ExecutionJobKind.GeoservicesImport }
        };
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
        return (store, durable);
    }

    private static void AssertLateExpiryOutcome(ExecutionJobRecord durable)
    {
        Assert.Equal(ExecutionJobStatus.Failed, durable.Status);
        Assert.Equal("license expired", durable.ErrorMessage);
        Assert.Empty(durable.ArtifactReferences);
        Assert.Empty(durable.CommittedEffectReferences);
        Assert.Null(durable.PercentComplete);
    }

    private static IJobExecutor CreateSuccessfulRecoveryExecutor()
    {
        var executor = Substitute.For<IJobExecutor, IJobOutcomeRecoveryExecutor>();
        executor.Kind.Returns(ExecutionJobKind.GeoservicesImport);
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

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Import;

public sealed partial class ImportJobExecutorTests
{
    [UnitTheory]
    [InlineData(GeoservicesImportStatus.Failed)]
    [InlineData(GeoservicesImportStatus.Cancelled)]
    public async Task ExecuteAsync_TerminalAfterCrash_RecoversCommittedRows(GeoservicesImportStatus terminalStatus)
    {
        var request = GeoservicesRequest(Guid.NewGuid().ToString("N"));
        var importer = Substitute.For<IGeoservicesImportService, IGeoservicesImportCommitRecovery>();
        ((IGeoservicesImportCommitRecovery)importer)
            .RecoverCommittedImportAsync(Arg.Any<GeoservicesImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(CommittedResult(request, GeoservicesImportStatus.NeedsReview));
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, manager, executor) = CreateGeoservicesExecutor(provider);
        var initial = GeoservicesImportProgress.CreateInitial(
            request.JobId!, request.ServiceUrl, request.LayerId, request.TableName);
        await manager.SubmitAsync(request, initial);
        await manager.ProgressStore.SetProgressAsync(request.JobId!, initial with
        {
            Status = terminalStatus, ErrorMessage = "Superseded failure"
        }, TimeSpan.FromMinutes(10));

        var result = await executor.ExecuteAsync(await ClaimedAttemptAsync(jobs, request.JobId!, attempt: 2),
            Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        result.CompletedWithCommittedEffects.Should().BeTrue();
        var progress = (await manager.ProgressStore.GetProgressAsync(request.JobId!))!;
        progress.Status.Should().Be(GeoservicesImportStatus.NeedsReview);
        progress.DataCommitted.Should().BeTrue();
        progress.FeaturesProcessed.Should().Be(4);
        await importer.DidNotReceive().ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(),
            Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ExecuteAsync_RestartWithCommittedRows_RecoversBeforeSourceValidationAndSecrets()
    {
        var request = GeoservicesRequest(Guid.NewGuid().ToString("N")) with
        {
            ServiceUrl = "http://127.0.0.1/private-source",
            Credentials = new() { AccessTokenSecretReference = "missing-source-credentials" }
        };
        var importer = Substitute.For<IGeoservicesImportService, IGeoservicesImportCommitRecovery>();
        ((IGeoservicesImportCommitRecovery)importer)
            .RecoverCommittedImportAsync(Arg.Any<GeoservicesImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(CommittedResult(request, GeoservicesImportStatus.NeedsReview));
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, manager, executor) = CreateGeoservicesExecutor(provider);
        await manager.SubmitAsync(request, GeoservicesImportProgress.CreateInitial(
            request.JobId!, request.ServiceUrl, request.LayerId, request.TableName));
        var context = Substitute.For<IJobExecutionContext>();

        var result = await executor.ExecuteAsync(await ClaimedAttemptAsync(jobs, request.JobId!, attempt: 2), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        result.CompletedWithCommittedEffects.Should().BeTrue();
        var progress = (await manager.ProgressStore.GetProgressAsync(request.JobId!))!;
        progress.Status.Should().Be(GeoservicesImportStatus.NeedsReview);
        progress.DataCommitted.Should().BeTrue();
        progress.FeaturesProcessed.Should().Be(4);
        await context.Received(1).RecordCommittedEffectAsync($"honua:import:{request.JobId}:committed", Arg.Any<CancellationToken>());
        await importer.DidNotReceive().ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(),
            Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ExecuteAsync_RecoveryCannotClassifyActiveWriter_PreservesQueuedProgressForRetry()
    {
        var request = GeoservicesRequest(Guid.NewGuid().ToString("N"));
        var importer = Substitute.For<IGeoservicesImportService, IGeoservicesImportCommitRecovery>();
        ((IGeoservicesImportCommitRecovery)importer)
            .RecoverCommittedImportAsync(Arg.Any<GeoservicesImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GeoservicesImportResult?>(new InvalidOperationException("Target writer is still active.")));
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, manager, executor) = CreateGeoservicesExecutor(provider);
        await manager.SubmitAsync(request, GeoservicesImportProgress.CreateInitial(
            request.JobId!, request.ServiceUrl, request.LayerId, request.TableName));

        var execute = async () => await executor.ExecuteAsync(await ClaimedAttemptAsync(jobs, request.JobId!, attempt: 2),
            Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        await execute.Should().ThrowAsync<InvalidOperationException>().WithMessage("Target writer is still active.");
        var progress = (await manager.ProgressStore.GetProgressAsync(request.JobId!))!;
        progress.Status.Should().Be(GeoservicesImportStatus.Queued);
        progress.DataCommitted.Should().BeFalse();
        await importer.DidNotReceive().ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(),
            Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ExecuteAsync_NoCommittedReceipt_StillValidatesSource()
    {
        var request = GeoservicesRequest(Guid.NewGuid().ToString("N")) with
        {
            ServiceUrl = "http://127.0.0.1/private-source"
        };
        var importer = Substitute.For<IGeoservicesImportService, IGeoservicesImportCommitRecovery>();
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, manager, executor) = CreateGeoservicesExecutor(provider);
        await manager.SubmitAsync(request, GeoservicesImportProgress.CreateInitial(
            request.JobId!, request.ServiceUrl, request.LayerId, request.TableName));

        var result = await executor.ExecuteAsync(await ClaimedAttemptAsync(jobs, request.JobId!, attempt: 2),
            Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        var progress = (await manager.ProgressStore.GetProgressAsync(request.JobId!))!;
        progress.Status.Should().Be(GeoservicesImportStatus.Failed);
        progress.DataCommitted.Should().BeFalse();
        await ((IGeoservicesImportCommitRecovery)importer).Received(1)
            .RecoverCommittedImportAsync(Arg.Is<GeoservicesImportRequest>(r => r.JobId == request.JobId), Arg.Any<CancellationToken>());
        await importer.DidNotReceive().ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(),
            Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ExecuteAsync_TerminalFailureWithoutReceipt_ReplaysAfterCommitRecovery()
    {
        var request = GeoservicesRequest(Guid.NewGuid().ToString("N"));
        var importer = Substitute.For<IGeoservicesImportService, IGeoservicesImportCommitRecovery>();
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, manager, executor) = CreateGeoservicesExecutor(provider);
        await manager.SubmitAsync(request, GeoservicesImportProgress.CreateInitial(
            request.JobId!, request.ServiceUrl, request.LayerId, request.TableName));
        await manager.ProgressStore.SetProgressAsync(request.JobId!, GeoservicesImportProgress.CreateInitial(
            request.JobId!, request.ServiceUrl, request.LayerId, request.TableName) with
        {
            Status = GeoservicesImportStatus.Failed,
            ErrorMessage = "Original failure"
        }, TimeSpan.FromMinutes(10));

        var result = await executor.ExecuteAsync(await ClaimedAttemptAsync(jobs, request.JobId!, attempt: 2),
            Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        (await manager.ProgressStore.GetProgressAsync(request.JobId!))!.ErrorMessage.Should().Be("Original failure");
        await ((IGeoservicesImportCommitRecovery)importer).Received(1)
            .RecoverCommittedImportAsync(Arg.Any<GeoservicesImportRequest>(), Arg.Any<CancellationToken>());
        await importer.DidNotReceive().ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(),
            Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ExecuteAsync_FirstAttempt_LeavesBusyTargetToImporterWithoutRecoveryWait()
    {
        var request = GeoservicesRequest(Guid.NewGuid().ToString("N"));
        var importer = Substitute.For<IGeoservicesImportService, IGeoservicesImportCommitRecovery>();
        ((IGeoservicesImportCommitRecovery)importer)
            .RecoverCommittedImportAsync(Arg.Any<GeoservicesImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GeoservicesImportResult?>(new InvalidOperationException("Target writer is still active.")));
        importer.ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(),
                Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(GeoservicesImportResult.CreateFailure(request.TableName, request.ServiceUrl, request.LayerId,
                "Another import is already writing the target table; this job did not modify it."));
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, manager, executor) = CreateGeoservicesExecutor(provider);
        await manager.SubmitAsync(request, GeoservicesImportProgress.CreateInitial(
            request.JobId!, request.ServiceUrl, request.LayerId, request.TableName));

        var result = await executor.ExecuteAsync(await ClaimedAttemptAsync(jobs, request.JobId!, attempt: 1),
            Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.IsRetryable.Should().BeFalse();
        await ((IGeoservicesImportCommitRecovery)importer).DidNotReceive()
            .RecoverCommittedImportAsync(Arg.Any<GeoservicesImportRequest>(), Arg.Any<CancellationToken>());
        await importer.Received(1).ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(),
            Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>());
    }
}

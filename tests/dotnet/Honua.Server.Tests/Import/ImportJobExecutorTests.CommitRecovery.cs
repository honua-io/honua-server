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

        var result = await executor.ExecuteAsync((await jobs.GetAsync(request.JobId!))!, context, CancellationToken.None);

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

        var execute = async () => await executor.ExecuteAsync((await jobs.GetAsync(request.JobId!))!,
            Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        await execute.Should().ThrowAsync<InvalidOperationException>().WithMessage("Target writer is still active.");
        var progress = (await manager.ProgressStore.GetProgressAsync(request.JobId!))!;
        progress.Status.Should().Be(GeoservicesImportStatus.Queued);
        progress.DataCommitted.Should().BeFalse();
        await importer.DidNotReceive().ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(),
            Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>());
    }
}

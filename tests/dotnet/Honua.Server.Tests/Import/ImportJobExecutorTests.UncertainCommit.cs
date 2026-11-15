// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Migration;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Import;

public sealed partial class ImportJobExecutorTests
{
    [UnitTest]
    public async Task ExecuteAsync_CommitAcknowledgementLost_RecoversReceiptOnNextAttempt()
    {
        var request = GeoservicesRequest(Guid.NewGuid().ToString("N"));
        var importer = Substitute.For<IGeoservicesImportService, IGeoservicesImportCommitRecovery>();
        importer.ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(),
            Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GeoservicesImportResult>(
                new GeoservicesImportCommitOutcomeUnknownException(new IOException("Connection lost"))));
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, manager, executor) = CreateGeoservicesExecutor(provider);
        await manager.SubmitAsync(request, GeoservicesImportProgress.CreateInitial(
            request.JobId!, request.ServiceUrl, request.LayerId, request.TableName));
        var record = (await jobs.GetAsync(request.JobId!))!;
        var context = Substitute.For<IJobExecutionContext>();

        Func<Task> firstAttempt = () => executor.ExecuteAsync(record, context, CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<GeoservicesImportCommitOutcomeUnknownException>();
        GeoservicesImportJobManager.IsTerminalStatus((await manager.ProgressStore.GetProgressAsync(request.JobId!))!.Status)
            .Should().BeFalse();
        ((IGeoservicesImportCommitRecovery)importer).RecoverCommittedImportAsync(
            Arg.Any<GeoservicesImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(CommittedResult(request, GeoservicesImportStatus.NeedsReview));
        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        result.CompletedWithCommittedEffects.Should().BeTrue();
        await importer.Received(1).ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(),
            Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>());
    }
}

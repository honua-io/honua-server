// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using NSubstitute;

namespace Honua.Server.Tests.Import;

public sealed partial class ImportJobExecutorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_GeoServerTerminalCallback_PreservesReturnedFailureCount()
    {
        var request = GeoServerRequest(Guid.NewGuid().ToString("N"));
        var importer = Substitute.For<IGeoServerImportService>();
        importer.ImportConfigurationAsync(Arg.Any<GeoServerImportRequest>(),
            Arg.Any<IProgress<GeoServerImportProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await DeliverTerminalCallbackAsync(call.Arg<IProgress<GeoServerImportProgress>>()!,
                    GeoServerImportProgress.CreateInitial(request.JobId!, request.GeoServerRestUrl, request.TargetHonuaUrl)
                        with { Status = GeoServerImportStatus.Failed, CompletedAt = DateTimeOffset.UtcNow });
                return GeoServerImportResult.CreateFailure(request.GeoServerRestUrl, request.TargetHonuaUrl, "Apply failed")
                    with { FailedResources = 3 };
            });
        using var provider = CreateGeoServerProvider(importer);
        var (jobs, manager, executor) = CreateGeoServerExecutor(provider);
        await manager.SubmitAsync(request, GeoServerImportProgress.CreateInitial(
            request.JobId!, request.GeoServerRestUrl, request.TargetHonuaUrl), CancellationToken.None);

        var result = await executor.ExecuteAsync((await jobs.GetAsync(request.JobId!))!,
            Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        (await manager.ProgressStore.GetProgressAsync(request.JobId!))!.FailedResources.Should().Be(3);
    }

    [Theory]
    [InlineData(GeoservicesImportStatus.Completed, ExecutionJobStatus.Succeeded)]
    [InlineData(GeoservicesImportStatus.NeedsReview, ExecutionJobStatus.Succeeded)]
    [InlineData(GeoservicesImportStatus.Failed, ExecutionJobStatus.Succeeded)]
    [InlineData(GeoservicesImportStatus.Cancelled, ExecutionJobStatus.Succeeded)]
    [InlineData(GeoservicesImportStatus.Completed, ExecutionJobStatus.Failed)]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_ProviderTerminalCallbackBeforeResult_PreservesCompleteFinalOutcome(
        GeoservicesImportStatus callbackStatus, ExecutionJobStatus expectedStatus)
    {
        using var license = new CancellationTokenSource();
        var policy = Substitute.For<ILicenseOperationPolicy>();
        policy.OperationCancellation.Returns(license.Token);
        var request = PublishedGeoservicesRequest(Guid.NewGuid().ToString("N"), 4);
        var importer = TerminalCallbackImporter(request, callbackStatus,
            expectedStatus == ExecutionJobStatus.Failed ? license.Cancel : () => { });
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, manager, executor) = CreateGeoservicesExecutor(provider, policy);
        await manager.SubmitAsync(request, GeoservicesImportProgress.CreateInitial(
            request.JobId!, request.ServiceUrl, request.LayerId, request.TableName));

        var result = await executor.ExecuteAsync((await jobs.GetAsync(request.JobId!))!,
            Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(expectedStatus);
        var progress = (await manager.ProgressStore.GetProgressAsync(request.JobId!))!;
        progress.Status.Should().Be(expectedStatus == ExecutionJobStatus.Failed ? GeoservicesImportStatus.Failed : GeoservicesImportStatus.NeedsReview);
        progress.DataCommitted.Should().BeTrue();
        progress.FeaturesProcessed.Should().Be(4);
        progress.PublishedLayerId.Should().Be(42);
        if (expectedStatus == ExecutionJobStatus.Failed)
        {
            progress.ErrorMessage.Should().Contain("license expired");
        }
    }

    private static IGeoservicesImportService TerminalCallbackImporter(GeoservicesImportRequest request,
        GeoservicesImportStatus callbackStatus, Action beforeReturn)
    {
        var importer = Substitute.For<IGeoservicesImportService>();
        importer.ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(),
            Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await DeliverTerminalCallbackAsync(call.Arg<IProgress<GeoservicesImportProgress>>()!,
                    GeoservicesImportProgress.CreateInitial(request.JobId!, request.ServiceUrl, request.LayerId, request.TableName)
                        with { Status = callbackStatus, FeaturesProcessed = 1, CompletedAt = DateTimeOffset.UtcNow });

                beforeReturn();

                return CommittedResult(request, GeoservicesImportStatus.NeedsReview);
            });
        return importer;
    }

    private static async Task DeliverTerminalCallbackAsync<TProgress>(IProgress<TProgress> progress,
        TProgress terminal)
    {
        var reporter = (Progress<TProgress>)progress;
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnProgress(object? sender, TProgress value) => delivered.TrySetResult(true);
        reporter.ProgressChanged += OnProgress;
        try
        {
            progress.Report(terminal);
            // Progress invokes its handler before the event, forcing it to run before finalization.
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            reporter.ProgressChanged -= OnProgress;
        }
    }
}

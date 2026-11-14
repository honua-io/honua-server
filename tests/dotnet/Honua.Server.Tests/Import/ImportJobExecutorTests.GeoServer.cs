// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.Server.Tests.Import;

public sealed partial class ImportJobExecutorTests
{
    [UnitTest]
    public async Task GeoServerJobExecutor_DoesNotReprocessTerminalFailedJob_WhenDuplicateDeliveryOccurs()
    {
        using var provider = CreateGeoServerProvider(new ThrowingGeoServerImportService(ThrowingBehavior.FailImmediately));
        var (jobs, jobManager, service) = CreateGeoServerExecutor(provider);

        const string jobId = "geoserver-fail";
        var request = GeoServerRequest(jobId);
        var progress = GeoServerImportProgress.CreateInitial(jobId, request.GeoServerRestUrl, request.TargetHonuaUrl);

        await jobManager.SubmitAsync(request, progress, CancellationToken.None);
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));

        using var cancellation = new CancellationTokenSource();
        var record = (await jobs.GetAsync(jobId))!;
        var execution = service.ExecuteAsync(record, Substitute.For<IJobExecutionContext>(), cancellation.Token);
        try
        {
            await WaitForAsync(
                async () => (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))?.Status == GeoServerImportStatus.Failed,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            await execution;
            await service.ExecuteAsync(record, Substitute.For<IJobExecutionContext>(), cancellation.Token);

            provider.GetRequiredService<ThrowingGeoServerImportService>().CallCount.Should().Be(1);
            (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))!.Status.Should().Be(GeoServerImportStatus.Failed);
            jobManager.ReadRequest((await jobs.GetAsync(jobId))!).Should().NotBeNull();
        }
        finally
        {
            cancellation.Cancel();
            await ImportExecutionTestJobs.ObserveAsync(execution);
        }
    }

    [UnitTest]
    public async Task GeoServerJobExecutor_PreservesCancelledState_WhenCancellationSurfacesAsGenericFailure()
    {
        using var provider = CreateGeoServerProvider(new ThrowingGeoServerImportService(ThrowingBehavior.ThrowAfterCancellation));
        var (jobs, jobManager, service) = CreateGeoServerExecutor(provider);

        const string jobId = "geoserver-cancel";
        var request = GeoServerRequest(jobId);
        var progress = GeoServerImportProgress.CreateInitial(jobId, request.GeoServerRestUrl, request.TargetHonuaUrl);

        await jobManager.SubmitAsync(request, progress, CancellationToken.None);
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));

        using var cancellation = new CancellationTokenSource();
        var record = (await jobs.GetAsync(jobId))!;
        var execution = service.ExecuteAsync(record, Substitute.For<IJobExecutionContext>(), cancellation.Token);
        try
        {
            var importService = provider.GetRequiredService<ThrowingGeoServerImportService>();
            await importService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            await jobManager.ProgressStore.SetProgressAsync(
                jobId,
                progress with
                {
                    Status = GeoServerImportStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Cancellation requested"
                },
                TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            cancellation.Cancel();
            await ImportExecutionTestJobs.ObserveAsync(execution);

            await WaitForAsync(
                async () => (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))?.Status == GeoServerImportStatus.Cancelled,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))!.Status.Should().Be(GeoServerImportStatus.Cancelled);
        }
        finally
        {
            cancellation.Cancel();
            await ImportExecutionTestJobs.ObserveAsync(execution);
        }
    }

    [UnitTest]
    public async Task GeoServerJobExecutor_WorkerInterruption_PreservesRecoverableProgress()
    {
        var importer = new ThrowingGeoServerImportService(ThrowingBehavior.ThrowAfterCancellation);
        using var provider = CreateGeoServerProvider(importer);
        var (jobs, jobManager, service) = CreateGeoServerExecutor(provider);
        const string jobId = "geoserver-worker-interrupted";
        var request = GeoServerRequest(jobId);
        await manager.SubmitAsync(request,
            GeoServerImportProgress.CreateInitial(jobId, request.GeoServerRestUrl, request.TargetHonuaUrl),
            CancellationToken.None);
        using var interruption = new CancellationTokenSource();
        var execution = executor.ExecuteAsync((await jobs.GetAsync(jobId))!,
            Substitute.For<IJobExecutionContext>(), interruption.Token);
        try
        {
            await importer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            interruption.Cancel();
            Func<Task> interruptedExecution = () => execution;
            await interruptedExecution.Should().ThrowAsync<OperationCanceledException>();

            var progress = (await manager.ProgressStore.GetProgressAsync(jobId))!;
            GeoServerImportJobManager.IsTerminalStatus(progress.Status).Should().BeFalse();
            progress.CompletedAt.Should().BeNull();
            (await jobs.GetAsync(jobId))!.CancellationRequestedAt.Should().BeNull();
            manager.ReadRequest((await jobs.GetAsync(jobId))!).Should().BeEquivalentTo(request);
        }
        finally
        {
            interruption.Cancel();
            await ImportExecutionTestJobs.ObserveAsync(execution);
        }
    }

    [UnitTest]
    public async Task GeoservicesJobExecutor_WorkerInterruption_PreservesRecoverableProgress()
    {
        var importer = new ThrowingGeoservicesImportService(ThrowingBehavior.ThrowAfterCancellation);
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, jobManager, service) = CreateGeoservicesExecutor(provider);
        const string jobId = "geoservices-worker-interrupted";
        var request = GeoservicesRequest(jobId) with
        {
            LayerId = 0,
            TableName = "interrupted_import",
            AutoPublish = false
        };
        await manager.SubmitAsync(request,
            GeoservicesImportProgress.CreateInitial(jobId, request.ServiceUrl, request.LayerId, request.TableName),
            CancellationToken.None);
        using var interruption = new CancellationTokenSource();
        var execution = executor.ExecuteAsync((await jobs.GetAsync(jobId))!,
            Substitute.For<IJobExecutionContext>(), interruption.Token);
        try
        {
            await importer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            interruption.Cancel();
            Func<Task> interruptedExecution = () => execution;
            await interruptedExecution.Should().ThrowAsync<OperationCanceledException>();

            var progress = (await manager.ProgressStore.GetProgressAsync(jobId))!;
            GeoservicesImportJobManager.IsTerminalStatus(progress.Status).Should().BeFalse();
            progress.CompletedAt.Should().BeNull();
            (await jobs.GetAsync(jobId))!.CancellationRequestedAt.Should().BeNull();
            manager.ReadRequest((await jobs.GetAsync(jobId))!).Should().BeEquivalentTo(request);
        }
        finally
        {
            interruption.Cancel();
            await ImportExecutionTestJobs.ObserveAsync(execution);
        }
    }

    [UnitTest]
    public async Task GeoServerJobExecutor_LicenseExpiry_RecordsFailure_WhenCancellationSurfacesAsGenericFailure()
    {
        using var expiry = new CancellationTokenSource();
        var policy = Substitute.For<ILicenseOperationPolicy>();
        policy.OperationCancellation.Returns(expiry.Token);
        using var provider = CreateGeoServerProvider(new ThrowingGeoServerImportService(ThrowingBehavior.ThrowAfterCancellation));
        var (jobs, jobManager, service) = CreateGeoServerExecutor(provider, policy);

        const string jobId = "geoserver-cancel";
        var request = GeoServerRequest(jobId);
        var progress = GeoServerImportProgress.CreateInitial(jobId, request.GeoServerRestUrl, request.TargetHonuaUrl);

        await jobManager.SubmitAsync(request, progress, CancellationToken.None);
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));

        using var cancellation = new CancellationTokenSource();
        var record = (await jobs.GetAsync(jobId))!;
        var execution = service.ExecuteAsync(record, Substitute.For<IJobExecutionContext>(), cancellation.Token);
        try
        {
            var importService = provider.GetRequiredService<ThrowingGeoServerImportService>();
            await importService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            expiry.Cancel();

            await WaitForAsync(
                async () => (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))?.Status == GeoServerImportStatus.Failed,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            var failed = (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))!;
            failed.Status.Should().Be(GeoServerImportStatus.Failed);
            failed.ErrorMessage.Should().Be("license expired");
            failed.CurrentPhase.Should().Contain("partial import is incomplete");
        }
        finally
        {
            cancellation.Cancel();
            await ImportExecutionTestJobs.ObserveAsync(execution);
        }
    }
}

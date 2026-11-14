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

[Collection("Unit")]
public sealed partial class ImportJobExecutorTests
{
    [UnitTest]
    public async Task GeoservicesJobExecutor_PersistsResultFields_WhenImportCompletes()
    {
        using var provider = CreateGeoservicesProvider(new SuccessfulGeoservicesImportService());
        var (jobs, jobManager, service) = CreateGeoservicesExecutor(provider);

        const string jobId = "geoservices-result";
        var request = GeoservicesRequest(jobId) with
        {
            LayerId = 2,
            TableName = "geoservices_result_test",
            AutoPublish = true,
            ServiceName = "imported-service"
        };
        var progress = GeoservicesImportProgress.CreateInitial(jobId, request.ServiceUrl, request.LayerId, request.TableName) with
        {
            ServiceName = request.ServiceName
        };

        await jobManager.SubmitAsync(request, progress, CancellationToken.None);
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));

        using var cancellation = new CancellationTokenSource();
        var record = (await jobs.GetAsync(jobId))!;
        var execution = service.ExecuteAsync(record, Substitute.For<IJobExecutionContext>(), cancellation.Token);
        try
        {
            await WaitForAsync(
                async () => (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))?.Status == GeoservicesImportStatus.Completed,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            var completed = await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false);
            completed.Should().NotBeNull();
            completed!.CurrentPhase.Should().Be("Import completed and layer published");
            completed.ServiceName.Should().Be("imported-service");
            completed.PublishedLayerId.Should().Be(42);
            completed.LayerId.Should().Be(42);
            completed.SourceKind.Should().Be("geoservices-rest");
            completed.SourceUrl.Should().Be(request.ServiceUrl);
            completed.SourceLayerName.Should().Be("Roads");
            completed.FidelityVerdict.Should().Be(MigrationFidelityVerdicts.Unverified);
        }
        finally
        {
            cancellation.Cancel();
            await ImportExecutionTestJobs.ObserveAsync(execution);
        }
    }

    [UnitTest]
    public async Task GeoservicesJobExecutor_PersistsNeedsReviewVerdictAndDifferences_WhenFidelityGateBlocks()
    {
        using var provider = CreateGeoservicesProvider(new NeedsReviewGeoservicesImportService());
        var (jobs, jobManager, service) = CreateGeoservicesExecutor(provider);

        const string jobId = "geoservices-needs-review";
        var request = GeoservicesRequest(jobId) with
        {
            LayerId = 3,
            TableName = "geoservices_review_test",
            AutoPublish = true,
            ServiceName = "imported-service"
        };
        var progress = GeoservicesImportProgress.CreateInitial(jobId, request.ServiceUrl, request.LayerId, request.TableName);

        await jobManager.SubmitAsync(request, progress, CancellationToken.None);
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));

        using var cancellation = new CancellationTokenSource();
        var record = (await jobs.GetAsync(jobId))!;
        var execution = service.ExecuteAsync(record, Substitute.For<IJobExecutionContext>(), cancellation.Token);
        try
        {
            await WaitForAsync(
                async () => (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))?.CompletedAt is not null,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            var stored = await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false);
            stored.Should().NotBeNull();
            stored!.Status.Should().Be(GeoservicesImportStatus.NeedsReview);
            stored.CurrentPhase.Should().Be("Import published but requires operator review (fidelity gate)");
            stored.ErrorMessage.Should().Be(NeedsReviewGeoservicesImportService.ReviewReason);
            stored.FailedFeatures.Should().Be(2);
            stored.PublishedLayerId.Should().Be(77);
            stored.FidelityVerdict.Should().Be(MigrationFidelityVerdicts.Incomplete);
            var difference = stored.FidelityDifferences.Should().ContainSingle().Subject;
            difference.Code.Should().Be(MigrationFidelityDifferenceCodes.RecordsLost);
            difference.Actual.Should().Be("2 dropped source records");
        }
        finally
        {
            cancellation.Cancel();
            await ImportExecutionTestJobs.ObserveAsync(execution);
        }
    }

    [UnitTest]
    public async Task GeoservicesJobExecutor_WhenCancelledAfterCommit_PersistsNeedsReviewInsteadOfCancelled()
    {
        var importer = new CommittedCancelGeoservicesImportService();
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, jobManager, service) = CreateGeoservicesExecutor(provider);

        const string jobId = "geoservices-cancel-after-commit";
        var request = GeoservicesRequest(jobId) with
        {
            LayerId = 4,
            TableName = "geoservices_cancel_after_commit",
            AutoPublish = true,
            ServiceName = "imported-service"
        };
        var progress = GeoservicesImportProgress.CreateInitial(jobId, request.ServiceUrl, request.LayerId, request.TableName);

        await jobManager.SubmitAsync(request, progress, CancellationToken.None);
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));

        using var cancellation = new CancellationTokenSource();
        var record = (await jobs.GetAsync(jobId))!;
        var execution = service.ExecuteAsync(record, Substitute.For<IJobExecutionContext>(), cancellation.Token);
        try
        {
            await importer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var running = await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false);
            running.Should().NotBeNull();
            await jobManager.ProgressStore.SetProgressAsync(
                jobId,
                running! with
                {
                    Status = GeoservicesImportStatus.Cancelled,
                    CurrentPhase = "Cancellation requested"
                },
                TimeSpan.FromMinutes(10));
            importer.Release.TrySetResult(true);

            await WaitForAsync(
                async () =>
                {
                    var stored = await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false);
                    return stored?.Status is GeoservicesImportStatus.NeedsReview or GeoservicesImportStatus.Failed;
                },
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            var completed = await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false);
            completed.Should().NotBeNull();
            completed!.Status.Should().Be(GeoservicesImportStatus.NeedsReview);
            completed.CurrentPhase.Should().Be("Import cancelled after data was committed");
            completed.FeaturesProcessed.Should().Be(4);
            completed.FailedFeatures.Should().Be(1);
            completed.ErrorMessage.Should().Contain("already committed");
        }
        finally
        {
            cancellation.Cancel();
            await ImportExecutionTestJobs.ObserveAsync(execution);
        }
    }

    [UnitTest]
    public async Task GeoservicesJobExecutor_DoesNotReprocessTerminalFailedJob_WhenDuplicateDeliveryOccurs()
    {
        using var provider = CreateGeoservicesProvider(new ThrowingGeoservicesImportService(ThrowingBehavior.FailImmediately));
        var (jobs, jobManager, service) = CreateGeoservicesExecutor(provider);

        const string jobId = "geoservices-fail";
        var request = GeoservicesRequest(jobId) with
        {
            LayerId = 0,
            TableName = "geoservices_fail_test",
            AutoPublish = false
        };
        var progress = GeoservicesImportProgress.CreateInitial(jobId, request.ServiceUrl, request.LayerId, request.TableName);

        await jobManager.SubmitAsync(request, progress, CancellationToken.None);
        jobManager.ReadRequest((await jobs.GetAsync(jobId))!).Should().NotBeNull();
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));

        using var cancellation = new CancellationTokenSource();
        var record = (await jobs.GetAsync(jobId))!;
        var execution = service.ExecuteAsync(record, Substitute.For<IJobExecutionContext>(), cancellation.Token);
        try
        {
            await WaitForAsync(
                async () => (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))?.Status == GeoservicesImportStatus.Failed,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            await execution;
            await service.ExecuteAsync(record, Substitute.For<IJobExecutionContext>(), cancellation.Token);

            provider.GetRequiredService<ThrowingGeoservicesImportService>().CallCount.Should().Be(1);
            (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))!.Status.Should().Be(GeoservicesImportStatus.Failed);
            jobManager.ReadRequest((await jobs.GetAsync(jobId))!).Should().NotBeNull();
        }
        finally
        {
            cancellation.Cancel();
            await ImportExecutionTestJobs.ObserveAsync(execution);
        }
    }

    [UnitTest]
    public async Task GeoservicesJobExecutor_PreservesCancelledState_WhenCancellationSurfacesAsGenericFailure()
    {
        using var provider = CreateGeoservicesProvider(new ThrowingGeoservicesImportService(ThrowingBehavior.ThrowAfterCancellation));
        var (jobs, jobManager, service) = CreateGeoservicesExecutor(provider);

        const string jobId = "geoservices-cancel";
        var request = GeoservicesRequest(jobId) with
        {
            LayerId = 0,
            TableName = "geoservices_cancel_test",
            AutoPublish = false
        };
        var progress = GeoservicesImportProgress.CreateInitial(jobId, request.ServiceUrl, request.LayerId, request.TableName);

        await jobManager.SubmitAsync(request, progress, CancellationToken.None);
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));

        using var cancellation = new CancellationTokenSource();
        var record = (await jobs.GetAsync(jobId))!;
        var execution = service.ExecuteAsync(record, Substitute.For<IJobExecutionContext>(), cancellation.Token);
        try
        {
            var importService = provider.GetRequiredService<ThrowingGeoservicesImportService>();
            await importService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            await jobManager.ProgressStore.SetProgressAsync(
                jobId,
                progress with
                {
                    Status = GeoservicesImportStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Cancellation requested"
                },
                TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            cancellation.Cancel();
            await ImportExecutionTestJobs.ObserveAsync(execution);

            await WaitForAsync(
                async () => (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))?.Status == GeoservicesImportStatus.Cancelled,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))!.Status.Should().Be(GeoservicesImportStatus.Cancelled);
        }
        finally
        {
            cancellation.Cancel();
            await ImportExecutionTestJobs.ObserveAsync(execution);
        }
    }

    [UnitTest]
    public async Task GeoservicesJobExecutor_LicenseExpiry_RecordsFailure_WhenCancellationSurfacesAsGenericFailure()
    {
        using var expiry = new CancellationTokenSource();
        var policy = Substitute.For<ILicenseOperationPolicy>();
        policy.OperationCancellation.Returns(expiry.Token);
        using var provider = CreateGeoservicesProvider(new ThrowingGeoservicesImportService(ThrowingBehavior.ThrowAfterCancellation));
        var (jobs, jobManager, service) = CreateGeoservicesExecutor(provider, policy);

        const string jobId = "geoservices-cancel";
        var request = GeoservicesRequest(jobId) with
        {
            LayerId = 0,
            TableName = "geoservices_cancel_test",
            AutoPublish = false
        };
        var progress = GeoservicesImportProgress.CreateInitial(jobId, request.ServiceUrl, request.LayerId, request.TableName);

        await jobManager.SubmitAsync(request, progress, CancellationToken.None);
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));

        using var cancellation = new CancellationTokenSource();
        var record = (await jobs.GetAsync(jobId))!;
        var execution = service.ExecuteAsync(record, Substitute.For<IJobExecutionContext>(), cancellation.Token);
        try
        {
            var importService = provider.GetRequiredService<ThrowingGeoservicesImportService>();
            await importService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            expiry.Cancel();

            await WaitForAsync(
                async () => (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))?.Status == GeoservicesImportStatus.Failed,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            var failed = (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))!;
            failed.Status.Should().Be(GeoservicesImportStatus.Failed);
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

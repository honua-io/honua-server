// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Import;
using Honua.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Import;

public sealed partial class ImportJobExecutorTests
{
    private static (IExecutionJobStore Jobs, GeoservicesImportJobManager Manager, GeoservicesImportJobExecutor Executor)
        CreateGeoservicesExecutor(ServiceProvider provider, ILicenseOperationPolicy? policy = null)
    {
        var progress = new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance);
        var jobs = ImportExecutionTestJobs.Create();
        var manager = new GeoservicesImportJobManager(
            progress, NullLogger<GeoservicesImportJobManager>.Instance, jobs, Substitute.For<IJobQueue>());
        var executor = new GeoservicesImportJobExecutor(provider.GetRequiredService<IServiceScopeFactory>(),
            manager, NullLogger<GeoservicesImportJobExecutor>.Instance, licensePolicy: policy);
        return (jobs, manager, executor);
    }

    private static (IExecutionJobStore Jobs, GeoServerImportJobManager Manager, GeoServerImportJobExecutor Executor)
        CreateGeoServerExecutor(ServiceProvider provider, ILicenseOperationPolicy? policy = null)
    {
        var progress = new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance);
        var jobs = ImportExecutionTestJobs.Create();
        var manager = new GeoServerImportJobManager(
            progress, NullLogger<GeoServerImportJobManager>.Instance, jobs, Substitute.For<IJobQueue>());
        var executor = new GeoServerImportJobExecutor(new ConfigurationBuilder().Build(), new TestHostEnvironment(),
            provider.GetRequiredService<IServiceScopeFactory>(), manager,
            NullLogger<GeoServerImportJobExecutor>.Instance, licensePolicy: policy);
        return (jobs, manager, executor);
    }

    private static GeoservicesImportRequest GeoservicesRequest(string jobId) => new()
    {
        JobId = jobId,
        ServiceUrl = "https://8.8.8.8/arcgis/rest/services/Test/FeatureServer",
        LayerId = 0,
        TableName = jobId.Replace('-', '_')
    };

    private static GeoServerImportRequest GeoServerRequest(string jobId) => new()
    {
        JobId = jobId,
        GeoServerRestUrl = "https://8.8.8.8/geoserver/rest",
        TargetHonuaUrl = "https://honua.example.com",
        DryRun = true
    };
    private static GeoservicesImportRequest PublishedGeoservicesRequest(string jobId, int layerId)
        => GeoservicesRequest(jobId) with
        {
            LayerId = layerId,
            AutoPublish = true,
            ServiceName = "imported-service"
        };

    private static Task RecordCancellationAsync(GeoservicesImportJobManager manager, GeoservicesImportProgress progress)
        => manager.ProgressStore.SetProgressAsync(progress.JobId, progress with
        {
            Status = GeoservicesImportStatus.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Cancellation requested"
        }, TimeSpan.FromMinutes(10));

    private static Task RecordCancellationAsync(GeoServerImportJobManager manager, GeoServerImportProgress progress)
        => manager.ProgressStore.SetProgressAsync(progress.JobId, progress with
        {
            Status = GeoServerImportStatus.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Cancellation requested"
        }, TimeSpan.FromMinutes(10));

}

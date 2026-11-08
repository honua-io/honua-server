// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Migration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Import;

[Collection("Unit")]
public sealed class GeoservicesImportTenantTests
{
    [UnitTheory]
    [InlineData(null, null)]
    [InlineData("published", "owner")]
    public async Task ProcessJobAsync_PersistedTarget_RestoresTenantBeforeResolvingImporter(string? publicationNamespace, string? tenantId)
    {
        var target = new GeoservicesImportTarget
        {
            ServiceName = "logical-parcels",
            ServiceId = "planned-service",
            PublicName = "Parcels",
            FolderId = "planned-folder",
            FolderName = "Survey",
            Namespace = publicationNamespace,
            TenantId = tenantId,
            TenantSource = TenantContextSource.Claim
        };
        var observed = new TaskCompletionSource<(GeoservicesImportTarget?, string?, TenantContextSource)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddScoped<RequestTenantContext>();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<RequestTenantContext>());
        services.AddScoped<IGeoservicesImportService>(provider =>
        {
            var tenant = provider.GetRequiredService<ITenantContext>();
            var tenantAtResolution = tenant.TenantId;
            var sourceAtResolution = tenant.Source;
            var importer = Substitute.For<IGeoservicesImportService>();
            importer.ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(), Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.ArgAt<GeoservicesImportRequest>(0);
                    observed.TrySetResult((request.Target, tenantAtResolution, sourceAtResolution));
                    return Task.FromResult(GeoservicesImportResult.CreateSuccess(request.TableName, request.ServiceUrl,
                        request.LayerId, 1, publishedLayerId: 42, serviceName: request.ServiceName));
                });
            return importer;
        });
        using var provider = services.BuildServiceProvider();
        var progressStore = new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance);
        var cache = provider.GetRequiredService<IDistributedCache>();
        using var jobs = new RedisImportJobManager(progressStore, cache,
            NullLogger<RedisImportJobManager>.Instance, new ImportTestEnvironment());
        var worker = new GeoservicesImportBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(), jobs,
            NullLogger<GeoservicesImportBackgroundService>.Instance);
        const string jobId = "planned-folder-import";
        var queued = new GeoservicesImportRequest
        {
            JobId = jobId,
            ServiceUrl = "https://8.8.8.8/arcgis/rest/services/Survey/Parcels/FeatureServer",
            LayerId = 0,
            TableName = "planned_parcels",
            ServiceName = target.ServiceName,
            Target = target
        };
        await jobs.RequestStore.SetProgressAsync(jobId, queued, TimeSpan.FromMinutes(10));
        var roundTrip = await jobs.RequestStore.GetProgressAsync(jobId);
        roundTrip!.Target.Should().BeEquivalentTo(target);
        await jobs.ProgressStore.SetProgressAsync(jobId,
            GeoservicesImportProgress.CreateInitial(jobId, queued.ServiceUrl, 0, queued.TableName), TimeSpan.FromMinutes(10));
        await jobs.JobQueue.EnqueueAsync(jobId);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var actual = await observed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            actual.Item1.Should().BeEquivalentTo(target);
            actual.Item2.Should().Be(tenantId);
            actual.Item3.Should().Be(TenantContextSource.Claim);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private sealed class ImportTestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "ImportTests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

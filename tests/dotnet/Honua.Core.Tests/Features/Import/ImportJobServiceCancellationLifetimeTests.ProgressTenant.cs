// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Tests.Features.Import;

public sealed partial class ImportJobServiceCancellationLifetimeTests
{
    private const string ReportedPhase = "Reported by importer";

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task InMemoryService_ImporterProgressReport_KeepsRequestTenant()
    {
        var importer = new ProgressReportingImportService();
        using var service = CreateInMemory(importer);

        await AssertReportedProgressKeepsTenantAsync(service);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task UniversalService_ImporterProgressReport_KeepsRequestTenant()
    {
        var importer = new ProgressReportingImportService();
        using var provider = CreateProvider(importer);
        using var service = CreateUniversal(provider, new MemoryProgressStore());

        await AssertReportedProgressKeepsTenantAsync(service);
    }

    private static async Task AssertReportedProgressKeepsTenantAsync(IImportJobService service)
    {
        var jobId = await service.QueueImportAsync(CreateRequest() with { TenantId = "tenant-a" }, 0);

        var reported = await WaitForReportedProgressAsync(service, jobId);

        reported.TenantId.Should().Be("tenant-a");
        await service.CancelJobAsync(jobId);
    }

    private static async Task<ImportProgress> WaitForReportedProgressAsync(IImportJobService service, string jobId)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeout)
        {
            var progress = await service.GetProgressAsync(jobId);
            if (progress?.CurrentPhase == ReportedPhase)
            {
                return progress;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for the importer's progress report.");
    }

    // Reports a fresh snapshot, as the streaming importer does, then waits to be cancelled.
    private sealed class ProgressReportingImportService : ImportServiceBase
    {
        public override async Task<ImportResult> ImportFileAsync(
            ImportRequest request,
            IProgress<ImportProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(ImportProgress.CreateInitial("importer", request.TableName, SupportedFileFormat.GeoJson)
                with { CurrentPhase = ReportedPhase });
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}

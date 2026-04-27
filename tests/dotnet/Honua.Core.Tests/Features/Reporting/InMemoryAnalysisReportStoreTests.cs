// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting;
using Honua.Core.Features.Reporting.Domain;
using Honua.Core.Features.Reporting.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Reporting;

/// <summary>
/// Verifies the bounded in-memory report store: round-trip semantics by
/// (jobId, contractVersion), invalidation, and that the underlying
/// MemoryCache enforces the configured size cap so the store cannot grow
/// unbounded as new job ids accumulate.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class InMemoryAnalysisReportStoreTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task StoreAsync_TryGetAsync_RoundTripsByJobAndContractVersion()
    {
        using var store = CreateStore(maxEntries: 16);
        var report = CreateReport(jobId: "job-1", resultPackageId: "pkg-1");

        await store.StoreAsync(report, CancellationToken.None);

        var fetched = await store.TryGetAsync(
            "job-1", ReportingConstants.ContractVersionV1, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.ReportId.Should().Be(report.ReportId);
        fetched.ResultPackageId.Should().Be("pkg-1");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task RemoveAsync_RemovesEntriesForGivenJob()
    {
        using var store = CreateStore(maxEntries: 16);
        await store.StoreAsync(CreateReport("job-A", "pkg-A"), CancellationToken.None);
        await store.StoreAsync(CreateReport("job-B", "pkg-B"), CancellationToken.None);

        await store.RemoveAsync("job-A", CancellationToken.None);

        (await store.TryGetAsync("job-A", ReportingConstants.ContractVersionV1, CancellationToken.None))
            .Should().BeNull();
        (await store.TryGetAsync("job-B", ReportingConstants.ContractVersionV1, CancellationToken.None))
            .Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task StoreAsync_BoundsCachedEntriesUnderConfiguredMax()
    {
        // SizeLimit is enforced by Microsoft.Extensions.Caching.Memory.MemoryCache.
        // With MaxEntries=2, inserting many distinct jobs cannot leave more than
        // ~2 entries retrievable after compaction; we don't assume strict LRU
        // ordering — only that the store does not retain every key indefinitely.
        using var store = CreateStore(maxEntries: 2);
        const int totalJobs = 32;

        for (var i = 0; i < totalJobs; i++)
        {
            await store.StoreAsync(
                CreateReport(jobId: $"job-{i}", resultPackageId: $"pkg-{i}"),
                CancellationToken.None);
        }

        var retained = 0;
        for (var i = 0; i < totalJobs; i++)
        {
            var hit = await store.TryGetAsync(
                $"job-{i}", ReportingConstants.ContractVersionV1, CancellationToken.None);
            if (hit is not null)
            {
                retained++;
            }
        }

        retained.Should().BeLessThan(totalJobs,
            "MemoryCache must evict at least some entries once the configured size cap is exceeded.");
    }

    private static InMemoryAnalysisReportStore CreateStore(int maxEntries) =>
        new(Options.Create(new ReportingConfiguration
        {
            Cache = new ReportingCacheConfiguration
            {
                MaxEntries = maxEntries,
                TtlMinutes = 60,
            },
        }));

    private static AnalysisReport CreateReport(string jobId, string resultPackageId) => new()
    {
        ReportId = $"{jobId}|{resultPackageId}",
        ReportContractVersion = ReportingConstants.ContractVersionV1,
        JobId = jobId,
        ResultPackageId = resultPackageId,
        ProcessId = "analytics.buffer-aggregate",
        ProcessFamily = "analytics",
        TemplateId = "analysis-report.analytics-buffer-aggregate",
        TemplateVersion = "1.0.0",
        Summary = new ResultSummary { Title = "T", Description = "D" },
        Sections = [],
        NarrativeMode = NarrativeMode.Deterministic,
        Provenance = new ProvenanceRecord
        {
            Sources = [],
            ProcessDefinitions = ["analytics.buffer-aggregate"],
            ExecutedAt = DateTimeOffset.UnixEpoch,
            GeneratedArtifactIds = [],
        },
        GeneratedAt = DateTimeOffset.UnixEpoch,
    };
}

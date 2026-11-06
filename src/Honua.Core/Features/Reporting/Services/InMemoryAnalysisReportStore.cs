// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Reporting.Services;

internal sealed class InMemoryAnalysisReportStore : IAnalysisReportStore, IDisposable
{
    // Invalidating a job must scan every contract version, or a new version would leave that job's reports in place.
    private static readonly string[] _knownContractVersions =
    [
        ReportingConstants.ContractVersionV1,
    ];

    private readonly MemoryCache _cache;
    private readonly TimeSpan _entryTtl;

    public InMemoryAnalysisReportStore(IOptions<ReportingConfiguration> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var cacheOptions = options.Value.Cache;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = Math.Max(1, cacheOptions.MaxEntries),
        });
        _entryTtl = TimeSpan.FromMinutes(Math.Max(1, cacheOptions.TtlMinutes));
    }

    public Task<AnalysisReport?> TryGetAsync(
        string jobId,
        string contractVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(contractVersion);

        _cache.TryGetValue<AnalysisReport>(BuildKey(jobId, contractVersion), out var report);
        return Task.FromResult(report);
    }

    public Task StoreAsync(AnalysisReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var entryOptions = new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = _entryTtl,
        };
        _cache.Set(BuildKey(report.JobId, report.ReportContractVersion), report, entryOptions);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        foreach (var version in _knownContractVersions)
        {
            _cache.Remove(BuildKey(jobId, version));
        }
        return Task.CompletedTask;
    }

    public void Dispose() => _cache.Dispose();

    private static string BuildKey(string jobId, string contractVersion)
        => string.Concat(jobId, "|", contractVersion);
}

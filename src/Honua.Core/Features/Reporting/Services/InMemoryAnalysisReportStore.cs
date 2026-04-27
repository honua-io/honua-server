// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Reporting.Services;

/// <summary>
/// In-memory <see cref="IAnalysisReportStore"/> backed by a bounded
/// <see cref="MemoryCache"/>. Entries are keyed by
/// <c>(jobId, contractVersion)</c> for O(1) lookup and are evicted under
/// configurable size and TTL caps so a long-lived server cannot grow
/// unbounded as new job ids accumulate.
/// </summary>
internal sealed class InMemoryAnalysisReportStore : IAnalysisReportStore, IDisposable
{
    /// <summary>
    /// Contract versions <see cref="RemoveAsync"/> must scan when invalidating
    /// a job. Extend when a new <c>ReportingConstants.ContractVersionVN</c> is
    /// introduced so RemoveAsync stays semantically "remove all reports for
    /// this jobId".
    /// </summary>
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

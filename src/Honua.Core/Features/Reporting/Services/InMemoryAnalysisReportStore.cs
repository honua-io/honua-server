// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Services;

/// <summary>
/// In-memory <see cref="IAnalysisReportStore"/>. Keys reports by
/// <c>(jobId, contractVersion, resultPackageId)</c> so a re-run that produces
/// a new result-package id transparently invalidates stale entries.
/// </summary>
internal sealed class InMemoryAnalysisReportStore : IAnalysisReportStore
{
    private readonly ConcurrentDictionary<string, AnalysisReport> _store = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<AnalysisReport?> TryGetAsync(
        string jobId,
        string contractVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(contractVersion);

        var report = _store
            .Where(kvp => kvp.Value.JobId == jobId
                          && kvp.Value.ReportContractVersion == contractVersion)
            .Select(kvp => kvp.Value)
            .FirstOrDefault();
        return Task.FromResult<AnalysisReport?>(report);
    }

    /// <inheritdoc />
    public Task StoreAsync(AnalysisReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var key = BuildKey(report.JobId, report.ReportContractVersion, report.ResultPackageId);
        _store[key] = report;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        foreach (var key in _store.Keys.Where(k => k.StartsWith(jobId + "|", StringComparison.Ordinal)).ToArray())
        {
            _store.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }

    private static string BuildKey(string jobId, string contractVersion, string resultPackageId)
        => string.Concat(jobId, "|", contractVersion, "|", resultPackageId);
}

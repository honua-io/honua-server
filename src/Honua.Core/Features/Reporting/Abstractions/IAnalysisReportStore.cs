// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Abstractions;

/// <summary>
/// Pluggable store for derived <see cref="AnalysisReport"/> envelopes. The
/// in-memory implementation backs the MVP; provider-backed durable stores can
/// implement the same surface without changes to the builder or renderers.
/// </summary>
public interface IAnalysisReportStore
{
    /// <summary>
    /// Returns a previously persisted report for the given job id and contract
    /// version, or <c>null</c> when the cache is empty.
    /// </summary>
    Task<AnalysisReport?> TryGetAsync(
        string jobId,
        string contractVersion,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores or replaces the report for the job. Implementations must be safe
    /// to call concurrently for distinct job ids.
    /// </summary>
    Task StoreAsync(AnalysisReport report, CancellationToken cancellationToken);

    /// <summary>
    /// Removes any persisted report for the job. Used when the upstream result
    /// package id changes for the same job id.
    /// </summary>
    Task RemoveAsync(string jobId, CancellationToken cancellationToken);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Share.Domain;

namespace Honua.Core.Features.Share.Abstractions;

/// <summary>
/// Read-only projection of Share traffic counters and time series for Console panels.
/// </summary>
/// <remarks>
/// Implementations must push aggregation to the backing store rather than streaming raw event
/// rows to the server. Until a traffic collection pass is wired, the default implementation
/// returns well-formed empty projections so the API shape stays stable for SDK projection.
/// </remarks>
public interface IShareTrafficStore
{
    /// <summary>
    /// Reads an aggregate or per-item traffic summary over a closed period.
    /// </summary>
    /// <param name="query">Read criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A traffic summary; counts are zero when no telemetry is available.</returns>
    Task<ShareTrafficSummary> GetSummaryAsync(
        ShareTrafficQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an aggregate or per-item traffic time series over a closed period.
    /// </summary>
    /// <param name="query">Read criteria including the bucket width.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A traffic series; buckets are present but zero-valued when no telemetry is available.</returns>
    Task<ShareTrafficSeries> GetSeriesAsync(
        ShareTrafficQuery query,
        CancellationToken cancellationToken = default);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>
/// Records geoprocessing (GP) tool invocations and surfaces a usage-ranked view of
/// the catalog so the most-invoked processes can be prioritized and ordered
/// (#2144). Recording is on the job execution hot path, so implementations must be
/// allocation-light, lock-light, and safe for concurrent access. The ranking read
/// path reflects every invocation recorded before it is called.
/// </summary>
public interface IProcessUsageTelemetry
{
    /// <summary>
    /// Records a single invocation of <paramref name="processId"/>, incrementing its
    /// total count (and the success/failure tally) and stamping it as most-recently
    /// used. Must not throw for telemetry reasons — a recording failure never fails
    /// the job it is measuring.
    /// </summary>
    /// <param name="processId">The canonical process id that was invoked.</param>
    /// <param name="succeeded">Whether the invocation completed successfully.</param>
    void RecordInvocation(string processId, bool succeeded);

    /// <summary>
    /// Returns the recorded processes ranked by total invocation count (descending),
    /// tie-broken by most-recent use (descending) and then process id (ascending) so
    /// the ordering is stable across reads. When <paramref name="limit"/> is a
    /// positive value the result is truncated to that many entries.
    /// </summary>
    /// <param name="limit">Optional maximum number of ranked entries to return.</param>
    IReadOnlyList<ProcessUsageRanking> GetRanking(int? limit = null);
}

/// <summary>
/// A single usage-ranked geoprocessing tool entry: its identifier, invocation
/// tallies, and last-used timestamp. Ordering within a ranking is defined by
/// <see cref="IProcessUsageTelemetry.GetRanking"/>.
/// </summary>
/// <param name="ProcessId">The canonical process id.</param>
/// <param name="InvocationCount">Total recorded invocations.</param>
/// <param name="SuccessCount">Invocations that completed successfully.</param>
/// <param name="FailureCount">Invocations that failed.</param>
/// <param name="LastInvokedAt">UTC timestamp of the most recent invocation.</param>
public sealed record ProcessUsageRanking(
    string ProcessId,
    long InvocationCount,
    long SuccessCount,
    long FailureCount,
    DateTimeOffset LastInvokedAt);

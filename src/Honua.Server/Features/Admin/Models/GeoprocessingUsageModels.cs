// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Usage-ranked view of the geoprocessing (GP) tool catalog (#2144). Surfaces the
/// most-invoked processes, ordered by total invocations, so operators can see
/// which tools to prioritize and how to tier the catalog.
/// </summary>
public sealed class GeoprocessingUsageRankingResponse
{
    /// <summary>
    /// Number of ranked tools returned in <see cref="Tools"/>.
    /// </summary>
    public required int Count { get; init; }

    /// <summary>
    /// Ranked GP tools, most-invoked first. Ordering is stable: descending by
    /// invocation count, then by most-recent use, then by process id.
    /// </summary>
    public required GeoprocessingToolUsageRankEntry[] Tools { get; init; }
}

/// <summary>
/// A single usage-ranked geoprocessing tool entry with its rank and recorded tallies.
/// </summary>
public sealed class GeoprocessingToolUsageRankEntry
{
    /// <summary>
    /// One-based position of this tool within the ranking.
    /// </summary>
    public required int Rank { get; init; }

    /// <summary>
    /// Canonical process id of the GP tool.
    /// </summary>
    public required string ProcessId { get; init; }

    /// <summary>
    /// Total recorded invocations.
    /// </summary>
    public required long InvocationCount { get; init; }

    /// <summary>
    /// Invocations that completed successfully.
    /// </summary>
    public required long SuccessCount { get; init; }

    /// <summary>
    /// Invocations that failed.
    /// </summary>
    public required long FailureCount { get; init; }

    /// <summary>
    /// UTC timestamp of the most recent invocation.
    /// </summary>
    public required DateTimeOffset LastInvokedAt { get; init; }
}

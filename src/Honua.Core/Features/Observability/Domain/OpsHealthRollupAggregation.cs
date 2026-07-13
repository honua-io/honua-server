// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

/// <summary>
/// Pure aggregation helpers for the ops-health rollup store. Used to merge per-replica rollup points into
/// a whole-cluster view (both for the live snapshot and the history read API).
/// </summary>
/// <remarks>
/// Percentiles cannot be recombined exactly from pre-aggregated percentiles without the raw distribution
/// (a NON-GOAL here — the mergeable sketch stays in the optional Prometheus bundle). A request-count-weighted
/// mean of per-replica percentiles is statistically wrong and, worse, dangerous: the p95 of nine healthy
/// replicas (100ms) and one sick replica (10s) means to ~1.1s and the sick instance vanishes under the SLO
/// threshold (#2809). Because the raw distribution is unavailable, cross-replica merging therefore takes the
/// MAX of each per-replica percentile — a documented conservative aggregation that never hides a sick replica
/// (the true pooled percentile is bounded above by the max of the per-replica percentiles). Request/error
/// counts are summed, since replicas serve disjoint traffic. Suitable for whole-cluster trend and SLO review.
/// </remarks>
public static class OpsHealthRollupAggregation
{
    /// <summary>
    /// Merges per-replica latency points for a single protocol into one whole-cluster point. Counts are
    /// summed (disjoint traffic); each percentile takes the MAX across replicas (a conservative aggregation
    /// that never hides a sick replica — see the type remarks and #2809), as does the observed maximum.
    /// </summary>
    /// <param name="protocol">The protocol the merged point describes.</param>
    /// <param name="points">The per-replica points (must be non-empty).</param>
    /// <returns>The merged whole-cluster latency point.</returns>
    public static OpsHealthLatencyPoint MergeAcrossReplicas(string protocol, IReadOnlyList<OpsHealthLatencyPoint> points)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("At least one point is required to merge.", nameof(points));
        }

        if (points.Count == 1)
        {
            return points[0] with { Protocol = protocol };
        }

        long requestCount = 0;
        long errorCount = 0;
        double maxP50 = 0;
        double maxP95 = 0;
        double maxP99 = 0;
        double maxMs = 0;

        foreach (var point in points)
        {
            requestCount += point.RequestCount;
            errorCount += point.ErrorCount;
            maxP50 = Math.Max(maxP50, point.P50Ms);
            maxP95 = Math.Max(maxP95, point.P95Ms);
            maxP99 = Math.Max(maxP99, point.P99Ms);
            maxMs = Math.Max(maxMs, point.MaxMs);
        }

        return new OpsHealthLatencyPoint
        {
            Protocol = protocol,
            RequestCount = requestCount,
            ErrorCount = errorCount,
            P50Ms = maxP50,
            P95Ms = maxP95,
            P99Ms = maxP99,
            MaxMs = maxMs,
        };
    }

    /// <summary>
    /// Merges per-replica vitals into one whole-cluster point: worst overall status, summed active DB
    /// connections, peak (max) queue/backlog signals, and averaged utilization/ratio gauges.
    /// </summary>
    /// <remarks>
    /// GP queue depth (total and the <c>"&lt;status&gt;|&lt;backend&gt;"</c> breakdown) and the alert-dispatch
    /// backlog are GLOBAL-view sections: every replica queries the same shared job/backlog store, so each
    /// point reports the whole cluster's queue, not a per-replica share. Merging them across replicas is a
    /// dedup, taken as a per-key MAX (summing would overcount by the replica count). Genuinely per-replica
    /// sections merge differently: active DB connections are summed, utilization/ratio gauges are averaged.
    /// Downsampling to coarser time tiers also stores the per-key MAX (peak within the bucket).
    /// </remarks>
    /// <param name="points">The per-replica vitals points (must be non-empty).</param>
    /// <returns>The merged whole-cluster vitals point.</returns>
    public static OpsHealthVitalsPoint MergeVitalsAcrossReplicas(IReadOnlyList<OpsHealthVitalsPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("At least one point is required to merge.", nameof(points));
        }

        if (points.Count == 1)
        {
            return points[0];
        }

        var worstStatus = "Healthy";
        var worstRank = int.MinValue;
        var gpQueueTotal = 0;
        long? alertPending = null;
        long? alertDeadLettered = null;
        double poolSum = 0;
        var poolCount = 0;
        var dbActive = 0;
        double cacheSum = 0;
        double errorSum = 0;
        var breakdown = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var point in points)
        {
            foreach (var bucket in point.GpQueueBreakdown)
            {
                // Global view reported by every replica — dedup with per-key max, never sum.
                breakdown[bucket.Key] = breakdown.TryGetValue(bucket.Key, out var existing)
                    ? Math.Max(existing, bucket.Value)
                    : bucket.Value;
            }

            var rank = StatusRank(point.OverallStatus);
            if (rank > worstRank)
            {
                worstRank = rank;
                worstStatus = point.OverallStatus;
            }

            gpQueueTotal = Math.Max(gpQueueTotal, point.GpQueueTotal);
            if (point.AlertPending is { } pending)
            {
                alertPending = Math.Max(alertPending ?? 0, pending);
            }

            if (point.AlertDeadLettered is { } dead)
            {
                alertDeadLettered = Math.Max(alertDeadLettered ?? 0, dead);
            }

            if (point.DbPoolUtilization is { } pool)
            {
                poolSum += pool;
                poolCount++;
            }

            dbActive += point.DbActiveConnections;
            cacheSum += point.CacheHitRatio;
            errorSum += point.ErrorRate;
        }

        return new OpsHealthVitalsPoint
        {
            OverallStatus = worstStatus,
            GpQueueTotal = gpQueueTotal,
            GpQueueBreakdown = breakdown,
            AlertPending = alertPending,
            AlertDeadLettered = alertDeadLettered,
            DbPoolUtilization = poolCount > 0 ? poolSum / poolCount : null,
            DbActiveConnections = dbActive,
            CacheHitRatio = cacheSum / points.Count,
            ErrorRate = errorSum / points.Count,
        };
    }

    /// <summary>
    /// Ranks a health status string by severity so the worst can be selected when merging. Unknown strings
    /// rank between healthy and degraded.
    /// </summary>
    /// <param name="status">The status string.</param>
    /// <returns>A severity rank (higher is worse).</returns>
    public static int StatusRank(string? status) => status switch
    {
        "Unhealthy" => 3,
        "Degraded" => 2,
        "Healthy" => 0,
        _ => 1,
    };
}

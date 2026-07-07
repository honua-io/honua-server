// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

/// <summary>
/// Pure aggregation helpers for the ops-health rollup store. Used to merge per-replica rollup points into
/// a whole-cluster view (both for the live snapshot and the history read API).
/// </summary>
/// <remarks>
/// Percentiles cannot be recombined exactly from pre-aggregated percentiles without the raw distribution
/// (a NON-GOAL here — that stays in the optional Prometheus bundle). Cross-replica merging therefore uses a
/// request-count-weighted mean of each percentile (falling back to the max when no in-window requests were
/// observed) and sums the request/error counts, since replicas serve disjoint traffic. This is a documented
/// approximation suitable for whole-cluster trend and SLO review.
/// </remarks>
public static class OpsHealthRollupAggregation
{
    /// <summary>
    /// Merges per-replica latency points for a single protocol into one whole-cluster point. Counts are
    /// summed (disjoint traffic); percentiles use a request-count-weighted mean and the max feeds the max.
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
        double weightedP50 = 0;
        double weightedP95 = 0;
        double weightedP99 = 0;
        double maxP50 = 0;
        double maxP95 = 0;
        double maxP99 = 0;
        double maxMs = 0;

        foreach (var point in points)
        {
            requestCount += point.RequestCount;
            errorCount += point.ErrorCount;
            var weight = point.RequestCount;
            weightedP50 += point.P50Ms * weight;
            weightedP95 += point.P95Ms * weight;
            weightedP99 += point.P99Ms * weight;
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
            P50Ms = requestCount > 0 ? weightedP50 / requestCount : maxP50,
            P95Ms = requestCount > 0 ? weightedP95 / requestCount : maxP95,
            P99Ms = requestCount > 0 ? weightedP99 / requestCount : maxP99,
            MaxMs = maxMs,
        };
    }

    /// <summary>
    /// Merges per-replica vitals into one whole-cluster point: worst overall status, summed active DB
    /// connections, peak (max) queue/backlog signals, and averaged utilization/ratio gauges.
    /// </summary>
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

        foreach (var point in points)
        {
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

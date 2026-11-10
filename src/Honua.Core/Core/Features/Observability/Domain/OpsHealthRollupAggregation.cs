// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

public static class OpsHealthRollupAggregation
{
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

        // Prefer the mergeable-distribution path: only usable when EVERY replica point carries a sketch, so a
        // point without one cannot silently drop its traffic from the merged percentiles.
        LatencyDistribution? mergedDistribution = LatencyDistribution.Empty;

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

            mergedDistribution = point.Distribution is { } distribution
                ? mergedDistribution?.Merge(distribution)
                : null;
        }

        if (mergedDistribution is not null && mergedDistribution.TotalCount > 0)
        {
            return new OpsHealthLatencyPoint
            {
                Protocol = protocol,
                RequestCount = requestCount,
                ErrorCount = errorCount,
                P50Ms = mergedDistribution.Quantile(0.50),
                P95Ms = mergedDistribution.Quantile(0.95),
                P99Ms = mergedDistribution.Quantile(0.99),
                MaxMs = maxMs,
                Distribution = mergedDistribution,
            };
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

    // Queue depth and the alert-dispatch backlog are global views, so they are not summed across replicas that each already report the whole store.
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

    public static int StatusRank(string? status) => status switch
    {
        "Unhealthy" => 3,
        "Degraded" => 2,
        "Healthy" => 0,
        _ => 1,
    };
}

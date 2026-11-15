// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record CatalogActivationEvidence(DateTimeOffset ActivatedAt, TimeSpan ServingWindow);

public static class CatalogActivationErrorWatch
{
    public static readonly TimeSpan BakePeriod = TimeSpan.FromMinutes(30);

    public static string? Evaluate(CatalogActivationEvidence activation, DateTimeOffset now,
        IReadOnlyList<OpsHealthLatencyRow> rows)
    {
        if (now < activation.ActivatedAt + activation.ServingWindow || now > activation.ActivatedAt + BakePeriod)
        {
            return null;
        }

        var baseline = rows.Where(row => row.BucketStart.AddMinutes(1) <= activation.ActivatedAt
                && row.BucketStart >= activation.ActivatedAt.AddMinutes(-3))
            .GroupBy(row => (row.ReplicaId, row.Point.Protocol))
            .ToDictionary(group => group.Key, group => group.MaxBy(row => row.BucketStart)!.Point);
        var after = rows.Where(row => row.BucketStart >= activation.ActivatedAt + activation.ServingWindow
                && row.BucketStart <= now && row.BucketStart >= now.AddMinutes(-2))
            .GroupBy(row => (row.ReplicaId, row.Point.Protocol));
        foreach (var group in after)
        {
            if (!baseline.TryGetValue(group.Key, out var before) || before.RequestCount < 20)
            {
                continue;
            }

            // Each point covers a rolling serving window; adding adjacent buckets would count traffic twice.
            var latest = group.GroupBy(row => row.BucketStart).OrderByDescending(bucket => bucket.Key)
                .Take(2).Select(bucket => bucket.First().Point).ToArray();
            if (latest.Length == 2 && latest.All(point => IsLatencyRegression(before, point)))
            {
                return $"Sustained serving latency increase after catalog activation for {group.Key.Protocol}.";
            }

            if (latest.Length == 2 && latest.All(point => IsRegression(before, point)))
            {
                return $"Sustained serving error increase after catalog activation for {group.Key.Protocol}.";
            }
        }

        return null;
    }

    private static bool IsLatencyRegression(OpsHealthLatencyPoint before, OpsHealthLatencyPoint after) =>
        after.RequestCount >= 20 && after.P95Ms >= 2000 && after.P95Ms >= before.P95Ms * 3
        && after.P95Ms - before.P95Ms >= 1000;

    private static bool IsRegression(OpsHealthLatencyPoint before, OpsHealthLatencyPoint after) =>
        after.RequestCount >= 20 && after.ErrorCount >= 5 && after.ErrorRate >= 0.20
        && after.ErrorRate - before.ErrorRate >= 0.10 && after.ErrorRate >= before.ErrorRate * 3;
}

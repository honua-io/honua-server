// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;

namespace Honua.Core.Features.Infrastructure.Monitoring;

// Callers must use one name per logical cache; Normalize only replaces a blank name and does not bound distinct cache_name values.
public static class HonuaCacheMetrics
{
    public const string CacheNameTag = "cache_name";

    private const string UnknownCacheName = "unknown";

    // Share the canonical "Honua" meter name with PerformanceMetrics and HonuaTelemetry so a single
    // OpenTelemetry exporter selector picks all Honua counters up. Using an independent Meter instance
    // avoids a circular dependency on Honua.ServiceDefaults (which itself references Honua.Core).
    private static readonly Meter Meter = new("Honua");

    // These counters declare no OpenTelemetry `unit`, and that omission is deliberate. The OTel
    // Prometheus exporter appends the mapped unit to the metric name, so the previous unit
    // "operations" exported them as `honua_cache_hits_total_operations_total` — a name nothing
    // queries. The shipped Cache Hit Ratio panel in
    // docker/monitoring/grafana/dashboards/honua-serving-overview.json asks for
    // `honua_cache_hits_total`, so the panel rendered blank with no error anywhere. See the
    // SLO-contract comment block in HonuaTelemetry.cs for the full mechanism (honua-release#5).

    public static readonly Counter<long> Hits = Meter.CreateCounter<long>(
        "honua_cache_hits_total",
        unit: null,
        "Total number of cache hits, partitioned by cache_name.");

    public static readonly Counter<long> Misses = Meter.CreateCounter<long>(
        "honua_cache_misses_total",
        unit: null,
        "Total number of cache misses, partitioned by cache_name.");

    public static readonly Counter<long> Evictions = Meter.CreateCounter<long>(
        "honua_cache_evictions_total",
        unit: null,
        "Total number of cache evictions, partitioned by cache_name.");

    public static void RecordHit(string? cacheName)
    {
        Hits.Add(1, new KeyValuePair<string, object?>(CacheNameTag, Normalize(cacheName)));
    }

    public static void RecordMiss(string? cacheName)
    {
        Misses.Add(1, new KeyValuePair<string, object?>(CacheNameTag, Normalize(cacheName)));
    }

    public static void RecordEviction(string? cacheName)
    {
        Evictions.Add(1, new KeyValuePair<string, object?>(CacheNameTag, Normalize(cacheName)));
    }

    private static string Normalize(string? cacheName)
    {
        return string.IsNullOrWhiteSpace(cacheName) ? UnknownCacheName : cacheName;
    }
}

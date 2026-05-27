// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Centralized cache hit / miss / eviction OpenTelemetry counters with a single
/// low-cardinality <c>cache_name</c> dimension so dashboards can compute per-cache hit ratios.
/// </summary>
/// <remarks>
/// <para>
/// Emits three counters on the shared <see cref="PerformanceMetrics.Meter"/> (meter name
/// <c>Honua</c>, matched by the OpenTelemetry exporter wired in <c>HonuaTelemetry</c>):
/// <list type="bullet">
///   <item><description><c>honua_cache_hits_total</c></description></item>
///   <item><description><c>honua_cache_misses_total</c></description></item>
///   <item><description><c>honua_cache_evictions_total</c></description></item>
/// </list>
/// </para>
/// <para>
/// Call sites should invoke <see cref="RecordHit"/> / <see cref="RecordMiss"/> / <see cref="RecordEviction"/>
/// directly, or wrap an <c>IMemoryCache</c> / <c>IDistributedCache</c> in an instrumented decorator that
/// delegates to these helpers. Cache names should be low-cardinality (one per logical cache surface) so
/// the metric series count remains bounded.
/// </para>
/// </remarks>
public static class HonuaCacheMetrics
{
    /// <summary>
    /// Tag key used to identify the logical cache surface (e.g. <c>response</c>, <c>query_results</c>, <c>tile</c>).
    /// </summary>
    public const string CacheNameTag = "cache_name";

    private const string UnknownCacheName = "unknown";

    // Share the canonical "Honua" meter name with PerformanceMetrics and HonuaTelemetry so a single
    // OpenTelemetry exporter selector picks all Honua counters up. Using an independent Meter instance
    // avoids a circular dependency on Honua.ServiceDefaults (which itself references Honua.Core).
    private static readonly Meter Meter = new("Honua");

    /// <summary>
    /// Counter incremented on every cache hit. Tagged with <see cref="CacheNameTag"/>.
    /// </summary>
    public static readonly Counter<long> Hits = Meter.CreateCounter<long>(
        "honua_cache_hits_total",
        "operations",
        "Total number of cache hits, partitioned by cache_name.");

    /// <summary>
    /// Counter incremented on every cache miss. Tagged with <see cref="CacheNameTag"/>.
    /// </summary>
    public static readonly Counter<long> Misses = Meter.CreateCounter<long>(
        "honua_cache_misses_total",
        "operations",
        "Total number of cache misses, partitioned by cache_name.");

    /// <summary>
    /// Counter incremented on every cache eviction. Tagged with <see cref="CacheNameTag"/>.
    /// </summary>
    /// <remarks>
    /// Increment from an <c>IMemoryCache</c> <c>PostEvictionCallback</c> or explicit
    /// remove/invalidate code paths so dashboards can correlate eviction rate with hit-ratio drops.
    /// </remarks>
    public static readonly Counter<long> Evictions = Meter.CreateCounter<long>(
        "honua_cache_evictions_total",
        "operations",
        "Total number of cache evictions, partitioned by cache_name.");

    /// <summary>
    /// Records a cache hit for the named cache.
    /// </summary>
    /// <param name="cacheName">Logical cache surface identifier. Falls back to <c>"unknown"</c> when null/empty.</param>
    public static void RecordHit(string? cacheName)
    {
        Hits.Add(1, new KeyValuePair<string, object?>(CacheNameTag, Normalize(cacheName)));
    }

    /// <summary>
    /// Records a cache miss for the named cache.
    /// </summary>
    /// <param name="cacheName">Logical cache surface identifier. Falls back to <c>"unknown"</c> when null/empty.</param>
    public static void RecordMiss(string? cacheName)
    {
        Misses.Add(1, new KeyValuePair<string, object?>(CacheNameTag, Normalize(cacheName)));
    }

    /// <summary>
    /// Records a cache eviction for the named cache.
    /// </summary>
    /// <param name="cacheName">Logical cache surface identifier. Falls back to <c>"unknown"</c> when null/empty.</param>
    public static void RecordEviction(string? cacheName)
    {
        Evictions.Add(1, new KeyValuePair<string, object?>(CacheNameTag, Normalize(cacheName)));
    }

    private static string Normalize(string? cacheName)
    {
        return string.IsNullOrWhiteSpace(cacheName) ? UnknownCacheName : cacheName;
    }
}

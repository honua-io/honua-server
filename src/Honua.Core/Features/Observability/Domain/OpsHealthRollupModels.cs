// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

/// <summary>
/// Downsampled retention tier for the persisted ops-health rollup store (#2553). Coarser tiers are
/// derived from the finest tier by the background sampler's maintenance pass and retained longer.
/// </summary>
public enum OpsHealthRollupTier
{
    /// <summary>1-minute buckets, retained ~24h. Written directly by each replica's sampler flush.</summary>
    OneMinute = 0,

    /// <summary>5-minute buckets, retained ~14d. Downsampled from <see cref="OneMinute"/>.</summary>
    FiveMinute = 1,

    /// <summary>Hourly buckets, retained ~90d. Downsampled from <see cref="OneMinute"/>.</summary>
    Hourly = 2,
}

/// <summary>
/// Per-protocol serving-latency rollup point. Percentiles are the rolling-window values observed by a
/// replica's <c>ServingLatencyAggregator</c> at flush time (not per-bucket deltas).
/// </summary>
public sealed record OpsHealthLatencyPoint
{
    /// <summary>Gets the GIS protocol family.</summary>
    public required string Protocol { get; init; }

    /// <summary>Gets the in-window request count.</summary>
    public required long RequestCount { get; init; }

    /// <summary>Gets the in-window server-error count (HTTP status &gt;= 500).</summary>
    public required long ErrorCount { get; init; }

    /// <summary>Gets the 50th-percentile duration in milliseconds.</summary>
    public required double P50Ms { get; init; }

    /// <summary>Gets the 95th-percentile duration in milliseconds.</summary>
    public required double P95Ms { get; init; }

    /// <summary>Gets the 99th-percentile duration in milliseconds.</summary>
    public required double P99Ms { get; init; }

    /// <summary>Gets the maximum duration in milliseconds.</summary>
    public required double MaxMs { get; init; }

    /// <summary>
    /// Gets the mergeable latency distribution sketch for this point, when the replica captured one (#2809).
    /// When present on every per-replica point, cross-replica merges recompute cluster percentiles from the
    /// summed distribution instead of averaging pre-aggregated percentiles (which hides a sick replica).
    /// <c>null</c> when unavailable (older rows, or a provider that does not persist the sketch), in which
    /// case the merge falls back to the request-weighted-mean approximation.
    /// </summary>
    public LatencyDistribution? Distribution { get; init; }

    /// <summary>Gets the server-error rate over the window (0.0 to 1.0).</summary>
    public double ErrorRate => RequestCount > 0 ? (double)ErrorCount / RequestCount : 0d;
}

/// <summary>
/// Non-latency ops vitals captured in a single rollup flush: overall status, GP queue depth,
/// alert-dispatch backlog / dead-letters, and DB pool / cache utilization.
/// </summary>
public sealed record OpsHealthVitalsPoint
{
    private static readonly IReadOnlyDictionary<string, int> EmptyBreakdown =
        new Dictionary<string, int>(capacity: 0);

    /// <summary>Gets the overall health roll-up status string (<c>Healthy</c>/<c>Degraded</c>/<c>Unhealthy</c>).</summary>
    public required string OverallStatus { get; init; }

    /// <summary>Gets the total active geoprocessing jobs (queued + provisioning + running).</summary>
    public required int GpQueueTotal { get; init; }

    /// <summary>
    /// Gets the GP queue depth broken down by status and backend, keyed <c>"&lt;status&gt;|&lt;backend&gt;"</c>
    /// (e.g. <c>"Queued|local"</c>) — the flattened form of the live snapshot's status×backend buckets.
    /// Like <see cref="GpQueueTotal"/>, this is a GLOBAL view (every replica queries the same shared job
    /// store), so cross-replica merges dedup with a per-key max rather than summing. Empty when no jobs are
    /// active or no execution-job store is registered.
    /// </summary>
    public IReadOnlyDictionary<string, int> GpQueueBreakdown { get; init; } = EmptyBreakdown;

    /// <summary>
    /// Gets the alert-dispatch pending backlog count, when available. Global-view section (shared backlog
    /// store): cross-replica merges dedup with a max.
    /// </summary>
    public long? AlertPending { get; init; }

    /// <summary>
    /// Gets the alert-dispatch dead-lettered count, when available. Global-view section (shared backlog
    /// store): cross-replica merges dedup with a max.
    /// </summary>
    public long? AlertDeadLettered { get; init; }

    /// <summary>Gets the database connection-pool utilization ratio (0.0 to 1.0), when available.</summary>
    public double? DbPoolUtilization { get; init; }

    /// <summary>Gets the number of active database connections.</summary>
    public required int DbActiveConnections { get; init; }

    /// <summary>Gets the cache hit ratio (0.0 to 1.0).</summary>
    public required double CacheHitRatio { get; init; }

    /// <summary>Gets the live HTTP server error rate (0.0 to 1.0).</summary>
    public required double ErrorRate { get; init; }
}

/// <summary>
/// A single per-replica ops-health flush: the local serving-latency percentiles per protocol plus the
/// local ops vitals, captured at <see cref="CapturedAt"/> and keyed by <see cref="ReplicaId"/>.
/// </summary>
public sealed record OpsHealthRollupSample
{
    /// <summary>Gets the flushing replica identifier.</summary>
    public required string ReplicaId { get; init; }

    /// <summary>Gets the capture (flush) time; the store truncates it to the 1-minute bucket boundary.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>Gets the per-protocol serving-latency points (may be empty when no traffic was observed).</summary>
    public required IReadOnlyList<OpsHealthLatencyPoint> Latency { get; init; }

    /// <summary>Gets the ops vitals point.</summary>
    public required OpsHealthVitalsPoint Vitals { get; init; }
}

/// <summary>A stored latency point tagged with its bucket and replica.</summary>
public sealed record OpsHealthLatencyRow
{
    /// <summary>Gets the replica the row was flushed by.</summary>
    public required string ReplicaId { get; init; }

    /// <summary>Gets the (UTC) bucket start.</summary>
    public required DateTimeOffset BucketStart { get; init; }

    /// <summary>Gets the latency point.</summary>
    public required OpsHealthLatencyPoint Point { get; init; }
}

/// <summary>A stored vitals point tagged with its bucket and replica.</summary>
public sealed record OpsHealthVitalsRow
{
    /// <summary>Gets the replica the row was flushed by.</summary>
    public required string ReplicaId { get; init; }

    /// <summary>Gets the (UTC) bucket start.</summary>
    public required DateTimeOffset BucketStart { get; init; }

    /// <summary>Gets the vitals point.</summary>
    public required OpsHealthVitalsPoint Point { get; init; }
}

/// <summary>
/// Hard retention caps per tier. Rows older than the cap are pruned automatically so the footprint stays
/// bounded (documented default footprint: single-digit MB for a small cluster).
/// </summary>
public sealed record OpsHealthRollupRetentionPolicy
{
    /// <summary>Gets the maximum age of retained 1-minute rows.</summary>
    public required TimeSpan OneMinuteRetention { get; init; }

    /// <summary>Gets the maximum age of retained 5-minute rows.</summary>
    public required TimeSpan FiveMinuteRetention { get; init; }

    /// <summary>Gets the maximum age of retained hourly rows.</summary>
    public required TimeSpan HourlyRetention { get; init; }

    /// <summary>Gets the default policy (1-min × 24h, 5-min × 14d, hourly × 90d).</summary>
    public static OpsHealthRollupRetentionPolicy Default { get; } = new()
    {
        OneMinuteRetention = TimeSpan.FromHours(24),
        FiveMinuteRetention = TimeSpan.FromDays(14),
        HourlyRetention = TimeSpan.FromDays(90),
    };

    /// <summary>Returns the retention cap for the given tier.</summary>
    /// <param name="tier">The rollup tier.</param>
    /// <returns>The maximum retained age for <paramref name="tier"/>.</returns>
    public TimeSpan RetentionFor(OpsHealthRollupTier tier) => tier switch
    {
        OpsHealthRollupTier.OneMinute => OneMinuteRetention,
        OpsHealthRollupTier.FiveMinute => FiveMinuteRetention,
        OpsHealthRollupTier.Hourly => HourlyRetention,
        _ => OneMinuteRetention,
    };
}

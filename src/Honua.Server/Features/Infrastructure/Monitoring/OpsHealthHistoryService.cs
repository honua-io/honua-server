// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Builds the cluster-aggregated ops-health history response from the persisted rollup store (#2553).
/// </summary>
internal interface IOpsHealthHistoryService
{
    /// <summary>Reads a history window and shapes it into the API response.</summary>
    /// <param name="query">The parsed window/resolution/breakdown query.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The history response (empty series when no store is registered or a read fails).</returns>
    Task<OpsHealthHistoryResponse> GetAsync(OpsHealthHistoryQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Parsed, validated query for the ops-health history endpoint.</summary>
internal sealed record OpsHealthHistoryQuery
{
    /// <summary>Gets the resolution tier.</summary>
    public required OpsHealthRollupTier Tier { get; init; }

    /// <summary>Gets the look-back window.</summary>
    public required TimeSpan Window { get; init; }

    /// <summary>Gets a value indicating whether to return a per-replica breakdown instead of a cluster merge.</summary>
    public required bool PerReplica { get; init; }

    /// <summary>Parses raw query parameters, clamping the window to the tier's retention cap.</summary>
    /// <param name="window">The window string (e.g. <c>1h</c>, <c>24h</c>, <c>7d</c>, <c>90m</c>), or <c>null</c> for the tier default.</param>
    /// <param name="resolution">The resolution string (<c>1m</c>/<c>5m</c>/<c>1h</c>), or <c>null</c> for <c>1m</c>.</param>
    /// <param name="perReplica">Whether to break the series down per replica.</param>
    /// <param name="error">Set to a message when a parameter is invalid.</param>
    /// <param name="query">The parsed query when successful.</param>
    /// <returns><see langword="true"/> when parsing succeeds.</returns>
    public static bool TryParse(string? window, string? resolution, bool perReplica, out string? error, out OpsHealthHistoryQuery query)
    {
        error = null;
        query = default!;

        if (!TryParseTier(resolution, out var tier))
        {
            error = $"Unsupported resolution '{resolution}'. Use one of: 1m, 5m, 1h.";
            return false;
        }

        var policy = OpsHealthRollupRetentionPolicy.Default;
        var cap = policy.RetentionFor(tier);

        TimeSpan windowSpan;
        if (string.IsNullOrWhiteSpace(window))
        {
            windowSpan = DefaultWindow(tier);
        }
        else if (!TryParseDuration(window, out windowSpan))
        {
            error = $"Invalid window '{window}'. Use a number with a unit: s, m, h, or d (e.g. 24h).";
            return false;
        }

        if (windowSpan <= TimeSpan.Zero)
        {
            error = "Window must be positive.";
            return false;
        }

        if (windowSpan > cap)
        {
            windowSpan = cap;
        }

        query = new OpsHealthHistoryQuery { Tier = tier, Window = windowSpan, PerReplica = perReplica };
        return true;
    }

    private static TimeSpan DefaultWindow(OpsHealthRollupTier tier) => tier switch
    {
        OpsHealthRollupTier.OneMinute => TimeSpan.FromHours(1),
        OpsHealthRollupTier.FiveMinute => TimeSpan.FromDays(1),
        OpsHealthRollupTier.Hourly => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(1),
    };

    private static bool TryParseTier(string? resolution, out OpsHealthRollupTier tier)
    {
        switch (resolution?.Trim().ToLowerInvariant())
        {
            case null or "" or "1m" or "60s" or "1min":
                tier = OpsHealthRollupTier.OneMinute;
                return true;
            case "5m" or "300s" or "5min":
                tier = OpsHealthRollupTier.FiveMinute;
                return true;
            case "1h" or "60m" or "hour" or "hourly":
                tier = OpsHealthRollupTier.Hourly;
                return true;
            default:
                tier = OpsHealthRollupTier.OneMinute;
                return false;
        }
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var trimmed = value.Trim();
        if (trimmed.Length < 2)
        {
            return false;
        }

        var unit = trimmed[^1];
        var numberPart = trimmed[..^1];
        if (!long.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return false;
        }

        duration = unit switch
        {
            's' or 'S' => TimeSpan.FromSeconds(amount),
            'm' or 'M' => TimeSpan.FromMinutes(amount),
            'h' or 'H' => TimeSpan.FromHours(amount),
            'd' or 'D' => TimeSpan.FromDays(amount),
            _ => TimeSpan.Zero,
        };

        return duration > TimeSpan.Zero;
    }
}

/// <inheritdoc />
internal sealed class OpsHealthHistoryService : IOpsHealthHistoryService
{
    private readonly IOpsHealthRollupStore? _store;

    public OpsHealthHistoryService(IOpsHealthRollupStore? store = null)
    {
        _store = store;
    }

    public async Task<OpsHealthHistoryResponse> GetAsync(OpsHealthHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var now = DateTimeOffset.UtcNow;
        var from = now - query.Window;

        IReadOnlyList<OpsHealthLatencyRow> latencyRows = [];
        IReadOnlyList<OpsHealthVitalsRow> vitalsRows = [];

        if (_store is not null)
        {
            // Fail-open: a store outage returns an empty history rather than a 500.
            try
            {
                latencyRows = await _store.ReadLatencyAsync(query.Tier, from, now, cancellationToken).ConfigureAwait(false);
                vitalsRows = await _store.ReadVitalsAsync(query.Tier, from, now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                latencyRows = [];
                vitalsRows = [];
            }
        }

        return new OpsHealthHistoryResponse
        {
            GeneratedAt = now,
            Resolution = ResolutionLabel(query.Tier),
            WindowSeconds = query.Window.TotalSeconds,
            From = from,
            To = now,
            PerReplica = query.PerReplica,
            Latency = BuildLatencySeries(latencyRows, query.PerReplica),
            Vitals = BuildVitalsSeries(vitalsRows, query.PerReplica),
        };
    }

    private static List<OpsHealthHistoryLatencySeries> BuildLatencySeries(IReadOnlyList<OpsHealthLatencyRow> rows, bool perReplica)
    {
        var series = new List<OpsHealthHistoryLatencySeries>();

        if (perReplica)
        {
            foreach (var group in rows
                .GroupBy(row => (row.Point.Protocol, row.ReplicaId))
                .OrderBy(group => group.Key.Protocol, StringComparer.Ordinal)
                .ThenBy(group => group.Key.ReplicaId, StringComparer.Ordinal))
            {
                var points = group
                    .OrderBy(row => row.BucketStart)
                    .Select(row => ToLatencyPoint(row.BucketStart, row.Point))
                    .ToList();
                series.Add(new OpsHealthHistoryLatencySeries
                {
                    Protocol = group.Key.Protocol,
                    ReplicaId = group.Key.ReplicaId,
                    Points = points,
                });
            }

            return series;
        }

        foreach (var protocolGroup in rows
            .GroupBy(row => row.Point.Protocol)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var points = protocolGroup
                .GroupBy(row => row.BucketStart)
                .OrderBy(bucket => bucket.Key)
                .Select(bucket =>
                {
                    var merged = OpsHealthRollupAggregation.MergeAcrossReplicas(
                        protocolGroup.Key,
                        bucket.Select(row => row.Point).ToList());
                    return ToLatencyPoint(bucket.Key, merged);
                })
                .ToList();

            series.Add(new OpsHealthHistoryLatencySeries
            {
                Protocol = protocolGroup.Key,
                ReplicaId = null,
                Points = points,
            });
        }

        return series;
    }

    private static List<OpsHealthHistoryVitalsPoint> BuildVitalsSeries(IReadOnlyList<OpsHealthVitalsRow> rows, bool perReplica)
    {
        if (perReplica)
        {
            return rows
                .OrderBy(row => row.BucketStart)
                .ThenBy(row => row.ReplicaId, StringComparer.Ordinal)
                .Select(row => ToVitalsPoint(row.BucketStart, row.ReplicaId, row.Point))
                .ToList();
        }

        return rows
            .GroupBy(row => row.BucketStart)
            .OrderBy(bucket => bucket.Key)
            .Select(bucket =>
            {
                var merged = OpsHealthRollupAggregation.MergeVitalsAcrossReplicas(bucket.Select(row => row.Point).ToList());
                return ToVitalsPoint(bucket.Key, replicaId: null, merged);
            })
            .ToList();
    }

    private static OpsHealthHistoryLatencyPoint ToLatencyPoint(DateTimeOffset bucketStart, OpsHealthLatencyPoint point) => new()
    {
        BucketStart = bucketStart,
        RequestCount = point.RequestCount,
        ErrorCount = point.ErrorCount,
        ErrorRate = point.ErrorRate,
        P50Ms = point.P50Ms,
        P95Ms = point.P95Ms,
        P99Ms = point.P99Ms,
        MaxMs = point.MaxMs,
    };

    private static OpsHealthHistoryVitalsPoint ToVitalsPoint(DateTimeOffset bucketStart, string? replicaId, OpsHealthVitalsPoint point) => new()
    {
        BucketStart = bucketStart,
        ReplicaId = replicaId,
        OverallStatus = point.OverallStatus,
        GpQueueTotal = point.GpQueueTotal,
        AlertPending = point.AlertPending,
        AlertDeadLettered = point.AlertDeadLettered,
        DbPoolUtilization = point.DbPoolUtilization,
        DbActiveConnections = point.DbActiveConnections,
        CacheHitRatio = point.CacheHitRatio,
        ErrorRate = point.ErrorRate,
    };

    private static string ResolutionLabel(OpsHealthRollupTier tier) => tier switch
    {
        OpsHealthRollupTier.OneMinute => "1m",
        OpsHealthRollupTier.FiveMinute => "5m",
        OpsHealthRollupTier.Hourly => "1h",
        _ => "1m",
    };
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Per-replica background sampler (#2553). On a configurable interval it captures this replica's local
/// ops-health sections (per-protocol serving latency + vitals), flushes them into the persisted rollup
/// store, periodically downsamples/prunes the store, and raises the in-process
/// <see cref="OpsHealthFlushSignal"/> with the freshly cluster-aggregated snapshot.
/// </summary>
/// <remarks>
/// The sampler is strictly fail-open: it never runs on a serving path, and any rollup-store outage is
/// caught and logged so serving is unaffected — the deployment simply degrades to snapshot-only. When no
/// rollup store is registered (non-Postgres providers) it still raises the flush signal so realtime
/// consumers keep working; only persistence and history are unavailable.
/// </remarks>
internal sealed partial class OpsHealthRollupSampler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<OpsHealthRollupOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly OpsHealthFlushSignal _flushSignal;
    private readonly ILogger<OpsHealthRollupSampler> _logger;

    private DateTimeOffset _nextMaintenanceUtc = DateTimeOffset.MinValue;

    public OpsHealthRollupSampler(
        IServiceScopeFactory scopeFactory,
        IOptions<OpsHealthRollupOptions> options,
        TimeProvider timeProvider,
        OpsHealthFlushSignal flushSignal,
        ILogger<OpsHealthRollupSampler> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _flushSignal = flushSignal ?? throw new ArgumentNullException(nameof(flushSignal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(options.FlushIntervalSeconds);
        if (interval <= TimeSpan.Zero)
        {
            interval = TimeSpan.FromSeconds(45);
        }

        try
        {
            // Randomized initial delay (5-15s) before the periodic loop. Two reasons: (a) it staggers
            // replicas that all restarted together so their flush/downsample/prune passes do not land on
            // the database in lockstep, and (b) short-lived hosts — integration-test servers especially —
            // are torn down before the first flush ever fires, keeping the sampler's background load off
            // hosts that never live long enough to produce useful history.
            var initialJitter = TimeSpan.FromSeconds(Random.Shared.Next(5, 16));
            await Task.Delay(initialJitter, stoppingToken).ConfigureAwait(false);

            // First flush runs one interval after the jitter so startup is never blocked.
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunFlushAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Runs a single flush pass. Exposed for deterministic testing.</summary>
    /// <param name="cancellationToken">A token to cancel the flush.</param>
    /// <returns>A task that completes when the flush pass finishes.</returns>
    internal async Task RunFlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            var snapshotService = provider.GetRequiredService<IOpsHealthSnapshotService>();
            var store = provider.GetService<IOpsHealthRollupStore>();

            // The cluster-aggregated snapshot (same DTO as GET ops-health) drives the realtime seam.
            var snapshot = await snapshotService.GetAsync(cancellationToken).ConfigureAwait(false);

            if (store is not null)
            {
                await PersistAsync(store, snapshot, cancellationToken).ConfigureAwait(false);
            }

            RaiseFlushed(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: this is a periodic background rollup flush. A single
        // failed iteration (snapshot/persist outage) must never disturb serving; fail
        // open by degrading to snapshot-only for this interval and keep sampling.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FlushFailed(_logger, ex);
        }
    }

    private async Task PersistAsync(IOpsHealthRollupStore store, OpsHealthSnapshotResponse snapshot, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var now = _timeProvider.GetUtcNow();

        // Persist THIS replica's local serving latency (not the cluster-merged view) so cross-replica
        // aggregation on read never double-counts. Vitals come from the composed snapshot.
        var local = HonuaTelemetry.GetServingLatencySnapshot();
        var latency = new List<OpsHealthLatencyPoint>(local.Protocols.Count);
        foreach (var protocol in local.Protocols)
        {
            latency.Add(new OpsHealthLatencyPoint
            {
                Protocol = protocol.Protocol,
                RequestCount = protocol.RequestCount,
                ErrorCount = protocol.ErrorCount,
                P50Ms = protocol.P50Ms,
                P95Ms = protocol.P95Ms,
                P99Ms = protocol.P99Ms,
                MaxMs = protocol.MaxMs,
                Distribution = protocol.Distribution,
            });
        }

        // Flatten the live snapshot's status×backend GP queue buckets into the persisted breakdown map
        // ("<status>|<backend>" keys) so history can answer which backend/status was backing up.
        var gpBreakdown = new Dictionary<string, int>(snapshot.Geoprocessing.Buckets.Count, StringComparer.Ordinal);
        foreach (var bucket in snapshot.Geoprocessing.Buckets)
        {
            gpBreakdown[$"{bucket.Status}|{bucket.Backend}"] = bucket.Count;
        }

        var sample = new OpsHealthRollupSample
        {
            ReplicaId = options.ResolveReplicaId(),
            CapturedAt = now,
            Latency = latency,
            Vitals = new OpsHealthVitalsPoint
            {
                OverallStatus = snapshot.OverallStatus,
                GpQueueTotal = snapshot.Geoprocessing.TotalActive,
                GpQueueBreakdown = gpBreakdown,
                AlertPending = snapshot.AlertDispatch.PendingCount,
                AlertDeadLettered = snapshot.AlertDispatch.DeadLetteredCount,
                DbPoolUtilization = snapshot.Database.ConnectionPoolUtilization,
                DbActiveConnections = snapshot.Database.ActiveConnections,
                CacheHitRatio = snapshot.Database.CacheHitRatio,
                ErrorRate = snapshot.Database.ErrorRate,
            },
        };

        await store.WriteSampleAsync(sample, cancellationToken).ConfigureAwait(false);

        if (now >= _nextMaintenanceUtc)
        {
            var pruned = await store.DownsampleAndPruneAsync(now, options.ToRetentionPolicy(), cancellationToken).ConfigureAwait(false);
            _nextMaintenanceUtc = now.AddSeconds(options.MaintenanceIntervalSeconds);
            MaintenanceCompleted(_logger, pruned);
        }
    }

    private void RaiseFlushed(OpsHealthSnapshotResponse snapshot)
    {
        try
        {
            _flushSignal.Raise(snapshot);
        }
        // Intentionally generic: this signals best-effort realtime subscribers after a
        // flush. A misbehaving subscriber must not break the background sampler loop.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FlushSubscriberFailed(_logger, ex);
        }
    }

    [LoggerMessage(EventId = 7460, Level = LogLevel.Warning, Message = "Ops-health rollup flush failed; degrading to snapshot-only for this interval.")]
    private static partial void FlushFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7461, Level = LogLevel.Warning, Message = "Ops-health rollup flush subscriber threw.")]
    private static partial void FlushSubscriberFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7462, Level = LogLevel.Debug, Message = "Ops-health rollup maintenance pruned {PrunedRows} rows.")]
    private static partial void MaintenanceCompleted(ILogger logger, int prunedRows);
}

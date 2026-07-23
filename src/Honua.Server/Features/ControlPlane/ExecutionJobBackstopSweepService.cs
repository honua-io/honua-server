// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>
/// Low-frequency backstop sweep for execution-job reconciliation. Ships with Phase 1 in BOTH
/// trigger modes: it re-drives any active execution job whose <c>UpdatedAt</c> has gone stale past
/// the configured threshold, so a dropped or missed state-change event self-heals.
/// <para>
/// Because every reconcile funnels through the dispatcher into the leased + CAS-guarded reconciler,
/// a backstop reconcile that races a real event (or the poll loop) is a safe no-op: whichever path
/// already advanced the job wins the CAS, and the late path observes a terminal/unchanged record.
/// The cadence is deliberately coarse (minutes, not seconds) so it costs almost nothing in the
/// healthy case yet bounds worst-case latency when events are lost.
/// </para>
/// This works on-prem unchanged and can equally be invoked by EventBridge Scheduler in the cloud.
/// </summary>
internal sealed partial class ExecutionJobBackstopSweepService(
    IExecutionJobStore jobStore,
    IOperationReconcileDispatcher dispatcher,
    IOptions<ControlPlaneTriggerOptions> options,
    ILogger<ExecutionJobBackstopSweepService> logger) : BackgroundService
{
    private readonly ControlPlaneTriggerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.BackstopInterval > TimeSpan.Zero
            ? _options.BackstopInterval
            : TimeSpan.FromMinutes(2);
        var staleThreshold = _options.StaleThreshold > TimeSpan.Zero
            ? _options.StaleThreshold
            : TimeSpan.FromSeconds(90);

        Log.BackstopStarted(logger, (int)interval.TotalSeconds, (int)staleThreshold.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(staleThreshold, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            // Intentionally generic: this is a long-running background backstop-sweep
            // loop. A single failed sweep iteration must not kill the host's
            // background service; log and retry on the next interval.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.BackstopSweepFailed(logger, ex);
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Reconciles every active, non-terminal execution job whose <c>UpdatedAt</c> is older than
    /// <paramref name="staleThreshold"/>. Fresh jobs are skipped so the sweep is a no-op when events
    /// (or the poll loop) are keeping records current. Exposed internally so tests can drive a single
    /// sweep deterministically without the timer loop.
    /// </summary>
    internal async Task SweepOnceAsync(TimeSpan staleThreshold, CancellationToken cancellationToken)
    {
        var active = await jobStore.ListActiveAsync(kind: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var cutoff = DateTimeOffset.UtcNow - staleThreshold;

        foreach (var job in active)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsTerminal(job.Status) || job.UpdatedAt > cutoff)
            {
                continue;
            }

            try
            {
                Log.BackstopReconciling(logger, job.OperationId, (int)(DateTimeOffset.UtcNow - job.UpdatedAt).TotalSeconds);
                await dispatcher
                    .ReconcileOnceAsync(new OperationRef(OperationKind.ExecutionJob, job.OperationId), cancellationToken)
                    .ConfigureAwait(false);
            }
            // Intentional catch-all: this is a per-job reconcile inside the sweep;
            // one stale job's failure must not stop the rest of the sweep from
            // reconciling the other active jobs.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.BackstopReconcileFailed(logger, job.OperationId, ex);
            }
        }
    }

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

    private static partial class Log
    {
        [LoggerMessage(9042, LogLevel.Information, "Started execution-job backstop sweep (interval {IntervalSeconds}s, stale threshold {StaleSeconds}s)")]
        public static partial void BackstopStarted(ILogger logger, int intervalSeconds, int staleSeconds);

        [LoggerMessage(9043, LogLevel.Debug, "Backstop reconciling stale execution job {OperationId} (stale {StaleSeconds}s)")]
        public static partial void BackstopReconciling(ILogger logger, string operationId, int staleSeconds);

        [LoggerMessage(9044, LogLevel.Warning, "Backstop reconcile failed for execution job {OperationId}")]
        public static partial void BackstopReconcileFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9045, LogLevel.Warning, "Execution-job backstop sweep cycle failed")]
        public static partial void BackstopSweepFailed(ILogger logger, Exception exception);
    }
}

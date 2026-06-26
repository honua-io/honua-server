// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>
/// Low-frequency backstop sweep for the workflow-store-backed reconcilers: deploy, metadata-release,
/// and coordinated-release. Phase 2 sibling of <see cref="ExecutionJobBackstopSweepService"/>; ships
/// in BOTH trigger modes so a dropped or missed event (deploy provider event, or a staged
/// self-continue signal) self-heals.
/// <para>
/// Sweeping all three non-terminal workflow kinds matters most for the staged releases: each
/// reconcile advances exactly one stage, so a lost continue signal would otherwise wedge a release
/// mid-flight under event mode. The sweep re-drives any active operation whose <c>UpdatedAt</c> has
/// gone stale past the threshold, which both completes a missed deploy observation and walks a staged
/// release forward one stage per cycle until it terminates.
/// </para>
/// <para>
/// Because every reconcile funnels through the dispatcher into the leased + CAS-guarded reconciler, a
/// backstop reconcile that races a real event (or the poll loop) is a safe no-op: whichever path
/// already advanced the operation wins, and the late path observes a terminal/unchanged record. The
/// cadence is deliberately coarse (minutes, not seconds) so it costs almost nothing in the healthy
/// case yet bounds worst-case latency when events are lost. Works on-prem unchanged and can equally
/// be invoked by EventBridge Scheduler in the cloud.
/// </para>
/// </summary>
internal sealed partial class WorkflowOperationBackstopSweepService(
    IWorkflowOperationStore workflowStore,
    IOperationReconcileDispatcher dispatcher,
    IOptions<ControlPlaneTriggerOptions> options,
    ILogger<WorkflowOperationBackstopSweepService> logger) : BackgroundService
{
    private static readonly (WorkflowOperationKind StoreKind, OperationKind OperationKind)[] SweptKinds =
    [
        (WorkflowOperationKind.Deploy, OperationKind.DeployWorkflow),
        (WorkflowOperationKind.MetadataRelease, OperationKind.MetadataRelease),
        (WorkflowOperationKind.CoordinatedRelease, OperationKind.CoordinatedRelease)
    ];

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
            catch (Exception ex)
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
    /// Reconciles every active, non-terminal deploy/metadata/coordinated operation whose
    /// <c>UpdatedAt</c> is older than <paramref name="staleThreshold"/>. Fresh operations are skipped
    /// so the sweep is a no-op when events (or the poll loop) are keeping records current. Exposed
    /// internally so tests can drive a single sweep deterministically without the timer loop.
    /// </summary>
    internal async Task SweepOnceAsync(TimeSpan staleThreshold, CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - staleThreshold;

        foreach (var (storeKind, operationKind) in SweptKinds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ListActiveAsync already filters terminal operations; the stale-time gate then skips
            // any operation an event/poll is keeping fresh.
            var active = await workflowStore.ListActiveAsync(storeKind, cancellationToken).ConfigureAwait(false);

            foreach (var operation in active)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (operation.UpdatedAt > cutoff)
                {
                    continue;
                }

                try
                {
                    Log.BackstopReconciling(
                        logger,
                        operationKind.ToString(),
                        operation.OperationId,
                        (int)(DateTimeOffset.UtcNow - operation.UpdatedAt).TotalSeconds);
                    await dispatcher
                        .ReconcileOnceAsync(new OperationRef(operationKind, operation.OperationId), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.BackstopReconcileFailed(logger, operationKind.ToString(), operation.OperationId, ex);
                }
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9050, LogLevel.Information, "Started workflow-operation backstop sweep (interval {IntervalSeconds}s, stale threshold {StaleSeconds}s)")]
        public static partial void BackstopStarted(ILogger logger, int intervalSeconds, int staleSeconds);

        [LoggerMessage(9051, LogLevel.Debug, "Backstop reconciling stale {Kind} operation {OperationId} (stale {StaleSeconds}s)")]
        public static partial void BackstopReconciling(ILogger logger, string kind, string operationId, int staleSeconds);

        [LoggerMessage(9052, LogLevel.Warning, "Backstop reconcile failed for {Kind} operation {OperationId}")]
        public static partial void BackstopReconcileFailed(ILogger logger, string kind, string operationId, Exception exception);

        [LoggerMessage(9053, LogLevel.Warning, "Workflow-operation backstop sweep cycle failed")]
        public static partial void BackstopSweepFailed(ILogger logger, Exception exception);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

// A lost continue signal would leave a staged release mid-flight, so the backstop re-drives a stale operation, and a race with a real event is a no-op under the lease.
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
        (WorkflowOperationKind.CoordinatedRelease, OperationKind.CoordinatedRelease),
        (WorkflowOperationKind.PreMigrationRestore, OperationKind.PreMigrationRestore)
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
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Intentionally generic: this is a long-running background sweep loop. A
                // single failed iteration must not kill the host's background service; log
                // and retry on the next interval.
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
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Intentional broad catch: this is a per-operation attempt inside the
                    // backstop sweep loop; one operation's reconcile failure must not abort
                    // the sweep of the remaining stale operations.
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

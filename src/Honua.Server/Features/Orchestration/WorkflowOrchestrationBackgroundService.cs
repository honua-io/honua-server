// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Background worker that reconciles active workflow runs in a leased, resumable loop.
/// Mirrors <see cref="ControlPlane.DeployWorkflowReconcilerBackgroundService"/>.
/// </summary>
internal sealed class WorkflowOrchestrationBackgroundService(
    IWorkflowRunStore runStore,
    WorkflowOrchestrationEngine engine,
    ILogger<WorkflowOrchestrationBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        OrchestrationLog.ReconcilerBackgroundServiceStarted(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var activeRuns = await runStore.ListActiveAsync(stoppingToken).ConfigureAwait(false);
                foreach (var run in activeRuns)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        await engine.ReconcileWorkflowRunAsync(run.RunId, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    // Intentional catch-all: this is a per-run reconcile inside the
                    // background poll loop; one run's failure must not stop the
                    // other active runs from being reconciled in this pass.
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        OrchestrationLog.ReconciliationFailed(logger, run.RunId, ex);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            // Intentionally generic: this is a long-running background reconciler
            // loop. A single failed poll iteration must not kill the host's
            // background service; log and retry on the next interval.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                OrchestrationLog.PollLoopFailed(logger, ex);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

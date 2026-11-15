// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Orchestration.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Features.Orchestration;

internal sealed class WorkflowOrchestrationBackgroundService(
    IWorkflowRunStore runStore,
    WorkflowOrchestrationEngine engine,
    ILogger<WorkflowOrchestrationBackgroundService> logger,
    PollWakeSignal<IWorkflowRunStore>? wake = null) : BackgroundService
{
    private readonly IdlePollPacer _pacer = new(wake);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        OrchestrationLog.ReconcilerBackgroundServiceStarted(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            _pacer.BeginPoll();
            var idle = false;
            try
            {
                var activeRuns = await runStore.ListActiveAsync(stoppingToken).ConfigureAwait(false);
                idle = activeRuns.Count == 0;
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
                await (idle ? _pacer.WaitIdleAsync(stoppingToken) : _pacer.WaitAfterWorkAsync(stoppingToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

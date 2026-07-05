// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Honua.ControlPlane;

/// <summary>
/// Periodically samples the durable execution-job store and publishes a per-(status, backend)
/// queue-depth snapshot for the <c>honua.execution.queue.depth</c> observable gauge. This is the
/// GP-plane analogue of the serving-plane request/pool telemetry: it exposes how many jobs are
/// queued, provisioning, and running per batch-compute backend so operators can see backlog
/// building before durations regress. Runs on the long-running host only (a host-lifecycle
/// concern), not inside a single cold-start-reconcile-exit Lambda invocation.
/// </summary>
internal sealed partial class ExecutionQueueDepthCollectorBackgroundService(
    IExecutionJobStore jobStore,
    ILogger<ExecutionQueueDepthCollectorBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.CollectorStarted(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var activeJobs = await jobStore.ListActiveAsync(kind: null, cancellationToken: stoppingToken).ConfigureAwait(false);
                var snapshot = ControlPlaneTelemetry.ComputeQueueDepth(activeJobs);
                ControlPlaneTelemetry.UpdateQueueDepth(snapshot);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.CollectorSweepFailed(logger, ex);
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

    private static partial class Log
    {
        [LoggerMessage(9040, LogLevel.Information, "Started execution-job queue-depth collector background service")]
        public static partial void CollectorStarted(ILogger logger);

        [LoggerMessage(9041, LogLevel.Warning, "Execution-job queue-depth collector sweep failed")]
        public static partial void CollectorSweepFailed(ILogger logger, Exception exception);
    }
}

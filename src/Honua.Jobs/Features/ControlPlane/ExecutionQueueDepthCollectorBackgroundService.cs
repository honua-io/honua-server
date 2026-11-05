// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Honua.ControlPlane;

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
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Deliberately broad: a single failed sweep (store outage, telemetry error)
                // must not crash this background service; log and retry next interval.
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

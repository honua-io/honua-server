// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Background service that sweeps active execution jobs for expired heartbeats
/// and timed-out executions, applying retry or terminal-failure policies.
/// </summary>
internal sealed partial class JobReconciliationService(
    IExecutionJobStore jobStore,
    IJobQueue jobQueue,
    IQueueClaimReconciler claimReconciler,
    ILogger<JobReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleClaimThreshold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.ReconciliationStarted(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepActiveJobsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.ReconciliationSweepFailed(logger, ex);
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        Log.ReconciliationStopped(logger);
    }

    private async Task SweepActiveJobsAsync(CancellationToken cancellationToken)
    {
        var activeJobs = await jobStore.ListActiveAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var reconciled = 0;

        foreach (var job in activeJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (job.Status is ExecutionJobStatus.Queued)
            {
                continue; // Not yet claimed; nothing to reconcile.
            }

            // Timeout takes precedence: timed-out jobs must fail terminally
            // even when the heartbeat has also expired and retries remain.
            if (ShouldExpireTimeout(job, now))
            {
                await HandleTimeoutExpiryAsync(job, now, cancellationToken).ConfigureAwait(false);
                reconciled++;
                continue;
            }

            if (ShouldExpireHeartbeat(job, now))
            {
                await HandleHeartbeatExpiryAsync(job, now, cancellationToken).ConfigureAwait(false);
                reconciled++;
            }
        }

        if (reconciled > 0)
        {
            Log.ReconciliationSweepCompleted(logger, reconciled, activeJobs.Count);
        }

        // Reconcile orphaned claims where the queue move succeeded but the
        // subsequent store update failed.
        await claimReconciler.ReconcileStaleClaimsAsync(StaleClaimThreshold, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool ShouldExpireHeartbeat(ExecutionJobRecord job, DateTimeOffset now)
    {
        if (job.Status is not (ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running))
        {
            return false;
        }

        var lastHeartbeat = job.LastHeartbeatAt ?? job.ClaimedAt;
        if (!lastHeartbeat.HasValue)
        {
            return false;
        }

        var policy = job.HeartbeatPolicy ?? JobHeartbeatPolicy.Default;
        return policy.IsExpired(lastHeartbeat.Value, now);
    }

    private static bool ShouldExpireTimeout(ExecutionJobRecord job, DateTimeOffset now)
    {
        if (job.Status is not (ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running))
        {
            return false;
        }

        var startedAt = job.ClaimedAt;
        if (!startedAt.HasValue)
        {
            return false;
        }

        var policy = job.TimeoutPolicy ?? JobTimeoutPolicy.Default;
        return policy.IsExpired(startedAt.Value, now);
    }

    private async Task HandleHeartbeatExpiryAsync(
        ExecutionJobRecord job,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var retryPolicy = job.RetryPolicy ?? JobRetryPolicy.Default;

        if (retryPolicy.ShouldRetry(job.AttemptCount))
        {
            Log.HeartbeatExpiredRetrying(logger, job.OperationId, job.AttemptCount, retryPolicy.MaxAttempts);

            var delay = retryPolicy.ComputeDelay(job.AttemptCount + 1);
            var abandoned = job with
            {
                Status = ExecutionJobStatus.Queued,
                ClaimedBy = null,
                ClaimedAt = null,
                LastHeartbeatAt = null,
                UpdatedAt = now,
                CurrentPhase = $"Retrying (attempt {job.AttemptCount + 1}/{retryPolicy.MaxAttempts})",
                PercentComplete = null,
                ErrorMessage = null,
                ProviderOperationId = null,
                CompletedAt = null,
                ArtifactReferences = Array.Empty<string>(),
                NextRetryAt = delay > TimeSpan.Zero ? now.Add(delay) : null
            };
            await jobStore.SetAsync(abandoned, cancellationToken: cancellationToken).ConfigureAwait(false);

            await jobQueue.RequeueAsync(
                job.OperationId,
                job.Priority,
                delay > TimeSpan.Zero ? delay : null,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Log.HeartbeatExpiredFailed(logger, job.OperationId, job.AttemptCount);

            var failed = job with
            {
                Status = ExecutionJobStatus.Failed,
                UpdatedAt = now,
                CompletedAt = now,
                ErrorMessage = $"Worker heartbeat expired after {job.AttemptCount} attempt(s).",
                CurrentPhase = "Failed (heartbeat expired)"
            };
            await jobStore.SetAsync(failed, cancellationToken: cancellationToken).ConfigureAwait(false);
            await jobQueue.RemoveAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleTimeoutExpiryAsync(
        ExecutionJobRecord job,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Log.TimeoutExpired(logger, job.OperationId);

        var failed = job with
        {
            Status = ExecutionJobStatus.Failed,
            UpdatedAt = now,
            CompletedAt = now,
            ErrorMessage = $"Job exceeded maximum execution duration of {(job.TimeoutPolicy ?? JobTimeoutPolicy.Default).MaxDuration}.",
            CurrentPhase = "Failed (timeout)"
        };
        await jobStore.SetAsync(failed, cancellationToken: cancellationToken).ConfigureAwait(false);
        await jobQueue.RemoveAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
    }

    private static partial class Log
    {
        [LoggerMessage(9040, LogLevel.Information, "Job reconciliation service started")]
        public static partial void ReconciliationStarted(ILogger logger);

        [LoggerMessage(9041, LogLevel.Information, "Job reconciliation service stopped")]
        public static partial void ReconciliationStopped(ILogger logger);

        [LoggerMessage(9042, LogLevel.Debug, "Reconciliation sweep completed: {Reconciled} reconciled out of {Total} active")]
        public static partial void ReconciliationSweepCompleted(ILogger logger, int reconciled, int total);

        [LoggerMessage(9043, LogLevel.Error, "Reconciliation sweep failed")]
        public static partial void ReconciliationSweepFailed(ILogger logger, Exception exception);

        [LoggerMessage(9044, LogLevel.Warning, "Heartbeat expired for job {OperationId}: retrying (attempt {AttemptCount}/{MaxAttempts})")]
        public static partial void HeartbeatExpiredRetrying(ILogger logger, string operationId, int attemptCount, int maxAttempts);

        [LoggerMessage(9045, LogLevel.Error, "Heartbeat expired for job {OperationId}: no retries remaining after {AttemptCount} attempts")]
        public static partial void HeartbeatExpiredFailed(ILogger logger, string operationId, int attemptCount);

        [LoggerMessage(9046, LogLevel.Error, "Job {OperationId} exceeded maximum execution duration")]
        public static partial void TimeoutExpired(ILogger logger, string operationId);
    }
}

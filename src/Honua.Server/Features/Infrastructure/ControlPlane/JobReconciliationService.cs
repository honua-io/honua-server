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
    ExecutionJobCancellationTokens cancellationTokens,
    IEnumerable<IJobTerminalCallback> terminalCallbacks,
    IExecutionLogStore? logStore,
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
                if (await HandleTimeoutExpiryAsync(job, now, cancellationToken).ConfigureAwait(false))
                {
                    reconciled++;
                }

                continue;
            }

            if (ShouldExpireHeartbeat(job, now))
            {
                if (await HandleHeartbeatExpiryAsync(job, now, cancellationToken).ConfigureAwait(false))
                {
                    reconciled++;
                }
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

    /// <returns><c>true</c> when state was actually mutated; <c>false</c> when skipped.</returns>
    private async Task<bool> HandleHeartbeatExpiryAsync(
        ExecutionJobRecord snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Re-read the current record to avoid overwriting a job that completed
        // between the sweep snapshot and this handler invocation.
        var current = await jobStore.GetAsync(snapshot.OperationId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (IsStaleSnapshot(snapshot, current))
        {
            Log.ReconciliationSkippedStale(logger, snapshot.OperationId, current?.Status.ToString() ?? "deleted");
            return false;
        }

        // Re-validate heartbeat expiry against the fresh record. A heartbeat
        // landing between the sweep snapshot and this handler means the worker
        // is still alive — skip the transition to avoid duplicate execution.
        var freshNow = DateTimeOffset.UtcNow;
        if (!ShouldExpireHeartbeat(current!, freshNow))
        {
            Log.ReconciliationSkippedHeartbeatRefreshed(logger, snapshot.OperationId);
            return false;
        }

        // If cancellation was durably requested before the heartbeat expired,
        // honour it with a terminal Cancelled state instead of retrying. The
        // worker either never observed the signal or crashed before acting on it.
        if (current!.CancellationRequestedAt.HasValue)
        {
            return await TransitionToCancelledFromDurableSignalAsync(current, snapshot.ClaimedBy!, cancellationToken).ConfigureAwait(false);
        }

        var retryPolicy = current.RetryPolicy ?? JobRetryPolicy.Default;

        if (retryPolicy.ShouldRetry(current.AttemptCount))
        {
            // Re-read before the retry write to catch cancellation signals
            // that arrived between the initial check and this write.
            var preRetry = await jobStore.GetAsync(current!.OperationId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (IsStaleSnapshot(snapshot, preRetry))
            {
                return false;
            }

            if (preRetry!.CancellationRequestedAt.HasValue)
            {
                return await TransitionToCancelledFromDurableSignalAsync(preRetry, snapshot.ClaimedBy!, cancellationToken).ConfigureAwait(false);
            }

            Log.HeartbeatExpiredRetrying(logger, preRetry.OperationId, preRetry.AttemptCount, retryPolicy.MaxAttempts);

            var delay = retryPolicy.ComputeDelay(preRetry.AttemptCount + 1);
            var abandoned = preRetry with
            {
                Status = ExecutionJobStatus.Queued,
                ClaimedBy = null,
                ClaimedAt = null,
                LastHeartbeatAt = null,
                UpdatedAt = now,
                CurrentPhase = $"Retrying (attempt {current.AttemptCount + 1}/{retryPolicy.MaxAttempts})",
                PercentComplete = null,
                ErrorMessage = null,
                ProviderOperationId = null,
                CompletedAt = null,
                ArtifactReferences = Array.Empty<string>(),
                NextRetryAt = delay > TimeSpan.Zero ? now.Add(delay) : null
            };
            if (!await jobStore.TrySetAsync(abandoned, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            ControlPlaneTelemetry.RecordExecutionTransition(preRetry, abandoned);

            // Revoke the stale CTS immediately after the authoritative store
            // transition to Queued, before the queue write. If RequeueAsync
            // fails, cancel paths will not delegate to the dead worker and
            // the stale-claim reconciler will repair the queue state.
            cancellationTokens.Revoke(preRetry.OperationId, snapshot.ClaimedBy!);

            await jobQueue.RequeueAsync(
                preRetry.OperationId,
                preRetry.Priority,
                delay > TimeSpan.Zero ? delay : null,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Re-read before the fail write to catch late cancellation signals.
            var preFail = await jobStore.GetAsync(current!.OperationId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (IsStaleSnapshot(snapshot, preFail))
            {
                return false;
            }

            if (preFail!.CancellationRequestedAt.HasValue)
            {
                return await TransitionToCancelledFromDurableSignalAsync(preFail, snapshot.ClaimedBy!, cancellationToken).ConfigureAwait(false);
            }

            Log.HeartbeatExpiredFailed(logger, preFail.OperationId, preFail.AttemptCount);

            var failed = preFail with
            {
                Status = ExecutionJobStatus.Failed,
                UpdatedAt = now,
                CompletedAt = now,
                ErrorMessage = $"Worker heartbeat expired after {preFail.AttemptCount} attempt(s).",
                CurrentPhase = "Failed (heartbeat expired)"
            };
            if (!await jobStore.TrySetAsync(failed, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            ControlPlaneTelemetry.RecordExecutionTransition(preFail, failed);
            cancellationTokens.Revoke(preFail.OperationId, snapshot.ClaimedBy!);

            try
            {
                await jobQueue.RemoveAsync(preFail.OperationId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.QueueRemovalFailed(logger, preFail.OperationId, ex);
            }

            if (logStore != null)
            {
                try
                {
                    await logStore.SetRetentionAsync(preFail.OperationId, LogRetention, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.LogRetentionFailed(logger, preFail.OperationId, ex);
                }
            }

            await NotifyTerminalAsync(failed, CancellationToken.None).ConfigureAwait(false);
        }

        return true;
    }

    /// <returns><c>true</c> when state was actually mutated; <c>false</c> when skipped.</returns>
    private async Task<bool> HandleTimeoutExpiryAsync(
        ExecutionJobRecord snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Re-read the current record to avoid overwriting a job that completed
        // between the sweep snapshot and this handler invocation.
        var current = await jobStore.GetAsync(snapshot.OperationId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (IsStaleSnapshot(snapshot, current))
        {
            Log.ReconciliationSkippedStale(logger, snapshot.OperationId, current?.Status.ToString() ?? "deleted");
            return false;
        }

        // Re-validate timeout expiry against the fresh record. A requeued-and-
        // reclaimed job (possibly by the same worker) will have a fresh ClaimedAt
        // that resets the timeout window — skip the transition to avoid failing
        // a new attempt that has not actually timed out.
        var freshNow = DateTimeOffset.UtcNow;
        if (!ShouldExpireTimeout(current!, freshNow))
        {
            Log.ReconciliationSkippedTimeoutRefreshed(logger, snapshot.OperationId);
            return false;
        }

        Log.TimeoutExpired(logger, current!.OperationId);

        var failed = current with
        {
            Status = ExecutionJobStatus.Failed,
            UpdatedAt = now,
            CompletedAt = now,
            ErrorMessage = $"Job exceeded maximum execution duration of {(current.TimeoutPolicy ?? JobTimeoutPolicy.Default).MaxDuration}.",
            CurrentPhase = "Failed (timeout)"
        };
        if (!await jobStore.TrySetAsync(failed, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        ControlPlaneTelemetry.RecordExecutionTransition(current, failed);

        // Signal and remove the stale CTS immediately after the authoritative
        // store transition, before the queue write.
        cancellationTokens.Revoke(current.OperationId, snapshot.ClaimedBy!);

        try
        {
            await jobQueue.RemoveAsync(current.OperationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.QueueRemovalFailed(logger, current.OperationId, ex);
        }

        if (logStore != null)
        {
            try
            {
                await logStore.SetRetentionAsync(current.OperationId, LogRetention, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.LogRetentionFailed(logger, current.OperationId, ex);
            }
        }

        await NotifyTerminalAsync(failed, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private async Task NotifyTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        foreach (var callback in terminalCallbacks)
        {
            try
            {
                await callback.OnTerminalAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.TerminalCallbackFailed(logger, job.OperationId, ex);
            }
        }
    }

    /// <summary>
    /// Shared cancel-from-durable-signal path used by heartbeat-expiry handlers
    /// when <see cref="ExecutionJobRecord.CancellationRequestedAt"/> is set.
    /// </summary>
    private async Task<bool> TransitionToCancelledFromDurableSignalAsync(
        ExecutionJobRecord job,
        string claimedBy,
        CancellationToken cancellationToken)
    {
        Log.HeartbeatExpiredCancellationHonoured(logger, job.OperationId);

        var now = DateTimeOffset.UtcNow;
        var cancelledJob = job with
        {
            Status = ExecutionJobStatus.Cancelled,
            UpdatedAt = now,
            CompletedAt = now,
            ErrorMessage = "Cancelled by operator (durable signal honoured by reconciler).",
            CurrentPhase = "Cancelled"
        };
        if (!await jobStore.TrySetAsync(cancelledJob, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        ControlPlaneTelemetry.RecordExecutionTransition(job, cancelledJob);
        cancellationTokens.Revoke(job.OperationId, claimedBy);

        try
        {
            await jobQueue.RemoveAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.QueueRemovalFailed(logger, job.OperationId, ex);
        }

        if (logStore != null)
        {
            try
            {
                await logStore.SetRetentionAsync(job.OperationId, LogRetention, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.LogRetentionFailed(logger, job.OperationId, ex);
            }
        }

        await NotifyTerminalAsync(cancelledJob, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Returns <c>true</c> when the fresh record is no longer the same actively
    /// claimed attempt captured in the sweep snapshot. This mirrors the worker-
    /// side ownership guard so the reconciler only mutates jobs that are still
    /// in an active state owned by the same claim.
    /// </summary>
    private static bool IsStaleSnapshot(ExecutionJobRecord snapshot, ExecutionJobRecord? current)
        => current is null
           || current.Status is not (ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running)
           || string.IsNullOrWhiteSpace(current.ClaimedBy)
           || !string.Equals(current.ClaimedBy, snapshot.ClaimedBy, StringComparison.Ordinal);

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

        [LoggerMessage(9047, LogLevel.Information, "Reconciliation skipped for job {OperationId}: current status is {Status} (state changed since sweep snapshot)")]
        public static partial void ReconciliationSkippedStale(ILogger logger, string operationId, string status);

        [LoggerMessage(9048, LogLevel.Information, "Reconciliation skipped for job {OperationId}: heartbeat refreshed since sweep snapshot")]
        public static partial void ReconciliationSkippedHeartbeatRefreshed(ILogger logger, string operationId);

        [LoggerMessage(9049, LogLevel.Information, "Reconciliation skipped for job {OperationId}: timeout no longer expired since sweep snapshot")]
        public static partial void ReconciliationSkippedTimeoutRefreshed(ILogger logger, string operationId);

        [LoggerMessage(9067, LogLevel.Information, "Heartbeat expired for job {OperationId}: honouring durable cancellation signal")]
        public static partial void HeartbeatExpiredCancellationHonoured(ILogger logger, string operationId);

        [LoggerMessage(9070, LogLevel.Warning, "Terminal callback failed for job {OperationId}; admin progress may be stale")]
        public static partial void TerminalCallbackFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9072, LogLevel.Warning, "Queue removal failed for terminal job {OperationId}; stale-claim reconciler will repair")]
        public static partial void QueueRemovalFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9074, LogLevel.Warning, "Log retention set failed for terminal job {OperationId}; execution logs may expire early")]
        public static partial void LogRetentionFailed(ILogger logger, string operationId, Exception exception);
    }
}

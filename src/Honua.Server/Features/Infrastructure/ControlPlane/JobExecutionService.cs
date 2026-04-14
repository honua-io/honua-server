// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Worker-side hosted service that claims jobs from the queue, dispatches them
/// to the appropriate <see cref="IJobExecutor"/>, manages heartbeat pumping
/// during execution, and finalizes job state on completion or failure.
/// </summary>
internal sealed partial class JobExecutionService(
    IJobQueue jobQueue,
    IExecutionJobStore jobStore,
    IEnumerable<IJobExecutor> executors,
    ExecutionJobCancellationTokens cancellationTokens,
    IExecutionLogStore? logStore,
    ILogger<JobExecutionService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);

    private readonly Dictionary<ExecutionJobKind, IJobExecutor> _executorMap =
        executors.ToDictionary(e => e.Kind);

    private readonly HashSet<ExecutionJobKind> _acceptedKinds =
        new(executors.Select(e => e.Kind));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = GenerateWorkerId();

        if (_acceptedKinds.Count == 0)
        {
            Log.NoExecutorsRegistered(logger, workerId);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }

            Log.WorkerStopped(logger, workerId);
            return;
        }

        Log.WorkerStarted(logger, workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            string? claimedId = null;

            try
            {
                claimedId = await jobQueue.TryClaimAsync(
                    workerId, _acceptedKinds, stoppingToken).ConfigureAwait(false);

                if (claimedId != null)
                {
                    await ProcessJobAsync(claimedId, workerId, stoppingToken).ConfigureAwait(false);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // If shutdown arrived during the pre-execution phase of
                // ProcessJobAsync, the job is still claimed but was never
                // handed to the executor try/catch that handles shutdown
                // requeue. Force-requeue here so the job returns to the
                // pending queue immediately instead of waiting for heartbeat
                // expiry.
                if (claimedId != null)
                {
                    try
                    {
                        var job = await jobStore.GetAsync(claimedId, CancellationToken.None).ConfigureAwait(false);
                        if (job != null && !IsTerminalOrNotOwnedBy(job, workerId))
                        {
                            await AbandonJobAsync(job, workerId, "Worker shutdown.",
                                CancellationToken.None, forceRequeue: true).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.PreExecShutdownCleanupFailed(logger, claimedId, ex);
                    }

                    cancellationTokens.Remove(claimedId, workerId);
                }

                break;
            }
            catch (Exception ex)
            {
                Log.ClaimLoopError(logger, ex);
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

        Log.WorkerStopped(logger, workerId);
    }

    private async Task ProcessJobAsync(string operationId, string workerId, CancellationToken stoppingToken)
    {
        var job = await jobStore.GetAsync(operationId, stoppingToken).ConfigureAwait(false);
        if (job == null)
        {
            Log.JobNotFoundDuringExecution(logger, operationId);
            await jobQueue.RemoveAsync(operationId, stoppingToken).ConfigureAwait(false);
            return;
        }

        if (!_executorMap.TryGetValue(job.Spec.Kind, out var executor))
        {
            Log.NoExecutorForKind(logger, operationId, job.Spec.Kind.ToString());
            await AbandonJobAsync(job, workerId, "No executor registered for job kind.", stoppingToken).ConfigureAwait(false);
            return;
        }

        // Register the per-job CTS before the ownership re-check and Running
        // transition so that operator cancellation arriving from this point is
        // delivered through the token rather than as a direct store write that
        // the subsequent Running transition could overwrite.
        var timeoutPolicy = job.TimeoutPolicy ?? JobTimeoutPolicy.Default;
        using var timeoutCts = new CancellationTokenSource(timeoutPolicy.MaxDuration);
        using var jobCts = cancellationTokens.CreateLinkedTokenSource(
            operationId, workerId, stoppingToken, timeoutCts.Token);

        // Re-read before promoting to Running to catch cancellations that arrived
        // after the claim but before the worker registered its CTS.
        job = await jobStore.GetAsync(operationId, stoppingToken).ConfigureAwait(false);
        if (job == null)
        {
            cancellationTokens.Remove(operationId, workerId);
            await jobQueue.RemoveAsync(operationId, stoppingToken).ConfigureAwait(false);
            return;
        }

        if (IsTerminalOrNotOwnedBy(job, workerId))
        {
            Log.TerminalStateSkipped(logger, operationId, job.Status.ToString());
            cancellationTokens.Remove(operationId, workerId);

            // Only remove the queue entry for terminal jobs. Requeued or reclaimed
            // jobs have a queue entry that belongs to the new attempt.
            if (IsTerminal(job.Status))
            {
                await jobQueue.RemoveAsync(operationId, stoppingToken).ConfigureAwait(false);
            }

            return;
        }

        // Transition to Running.
        var now = DateTimeOffset.UtcNow;
        var running = job with
        {
            Status = ExecutionJobStatus.Running,
            UpdatedAt = now,
            LastHeartbeatAt = now,
            CurrentPhase = "Running"
        };
        await jobStore.SetAsync(running, cancellationToken: stoppingToken).ConfigureAwait(false);

        Log.JobExecutionStarted(logger, operationId, executor.Kind.ToString());

        // Create execution context with heartbeat pump.
        using var context = new JobExecutionContext(
            operationId, workerId, jobStore, logStore, job.HeartbeatPolicy ?? JobHeartbeatPolicy.Default, logger);

        // Start heartbeat pump in background.
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(jobCts.Token);
        var heartbeatTask = context.RunHeartbeatPumpAsync(heartbeatCts.Token);

        // Stops the heartbeat pump and waits for it to finish so that no
        // in-flight heartbeat write can clobber the terminal-state update.
        async Task StopHeartbeatPumpAsync()
        {
            await heartbeatCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation reaches the pump.
            }
            catch (Exception ex)
            {
                Log.HeartbeatPumpFaulted(logger, operationId, ex);
            }
        }

        try
        {
            var result = await executor.ExecuteAsync(running, context, jobCts.Token).ConfigureAwait(false);
            await StopHeartbeatPumpAsync().ConfigureAwait(false);

            if (result.Status == ExecutionJobStatus.Succeeded)
            {
                await FinalizeJobAsync(operationId, workerId, result, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                // Executor returned failure — route through retry policy.
                // Thread executor warnings so they are persisted on terminal
                // failure and logged to structured execution logs on retry.
                await AbandonJobAsync(running, workerId, result.ErrorMessage ?? "Execution failed.",
                    CancellationToken.None, result.Warnings).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await StopHeartbeatPumpAsync().ConfigureAwait(false);
            // Worker is shutting down; always requeue regardless of retry budget
            // because this is an infrastructure event, not an execution failure.
            // Use CancellationToken.None so store/queue cleanup can complete
            // after the host stopping signal has fired.
            await AbandonJobAsync(running, workerId, "Worker shutdown.",
                CancellationToken.None, forceRequeue: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            await StopHeartbeatPumpAsync().ConfigureAwait(false);
            Log.JobTimedOut(logger, operationId, timeoutPolicy.MaxDuration);
            // Timeout — terminal failure, do not retry.
            await TerminateJobAsync(operationId, workerId, ExecutionJobStatus.Failed,
                $"Execution timed out after {timeoutPolicy.MaxDuration}.", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopHeartbeatPumpAsync().ConfigureAwait(false);
            Log.JobCancelledByOperator(logger, operationId);
            // Operator cancellation — terminal cancelled state.
            await TerminateJobAsync(operationId, workerId, ExecutionJobStatus.Cancelled,
                "Cancelled by operator.", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await StopHeartbeatPumpAsync().ConfigureAwait(false);
            Log.JobExecutionFailed(logger, operationId, ex);
            // Execution exception — route through retry policy.
            await AbandonJobAsync(running, workerId, ex.Message, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            cancellationTokens.Remove(operationId, workerId);
        }
    }

    private async Task FinalizeJobAsync(
        string operationId,
        string workerId,
        JobExecutionResult result,
        CancellationToken cancellationToken)
    {
        var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return;
        }

        if (IsTerminalOrNotOwnedBy(job, workerId))
        {
            Log.TerminalStateSkipped(logger, operationId, job.Status.ToString());
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var final = job with
        {
            Status = result.Status,
            UpdatedAt = now,
            CompletedAt = now,
            ErrorMessage = result.ErrorMessage,
            Warnings = result.Warnings,
            PercentComplete = result.Status == ExecutionJobStatus.Succeeded ? 100 : job.PercentComplete,
            CurrentPhase = result.Status == ExecutionJobStatus.Succeeded ? "Completed" : "Failed"
        };

        await jobStore.SetAsync(final, cancellationToken: cancellationToken).ConfigureAwait(false);
        await jobQueue.RemoveAsync(operationId, cancellationToken).ConfigureAwait(false);

        if (logStore != null)
        {
            await logStore.SetRetentionAsync(operationId, LogRetention, cancellationToken).ConfigureAwait(false);
        }

        Log.JobExecutionCompleted(logger, operationId, result.Status.ToString());
    }

    private async Task TerminateJobAsync(
        string operationId,
        string workerId,
        ExecutionJobStatus terminalStatus,
        string reason,
        CancellationToken cancellationToken)
    {
        var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return;
        }

        if (IsTerminalOrNotOwnedBy(job, workerId))
        {
            Log.TerminalStateSkipped(logger, operationId, job.Status.ToString());
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var terminal = job with
        {
            Status = terminalStatus,
            UpdatedAt = now,
            CompletedAt = now,
            ErrorMessage = reason,
            CurrentPhase = terminalStatus == ExecutionJobStatus.Cancelled ? "Cancelled" : "Failed"
        };

        await jobStore.SetAsync(terminal, cancellationToken: cancellationToken).ConfigureAwait(false);
        await jobQueue.RemoveAsync(operationId, cancellationToken).ConfigureAwait(false);

        if (logStore != null)
        {
            await logStore.SetRetentionAsync(operationId, LogRetention, cancellationToken).ConfigureAwait(false);
        }

        Log.JobExecutionCompleted(logger, operationId, terminalStatus.ToString());
    }

    private async Task AbandonJobAsync(
        ExecutionJobRecord job,
        string workerId,
        string reason,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? warnings = null,
        bool forceRequeue = false)
    {
        // Re-read to capture progress and artifact updates made during execution.
        var current = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false) ?? job;

        if (IsTerminalOrNotOwnedBy(current, workerId))
        {
            Log.TerminalStateSkipped(logger, current.OperationId, current.Status.ToString());
            return;
        }

        var retryPolicy = current.RetryPolicy ?? JobRetryPolicy.Default;

        if (forceRequeue || retryPolicy.ShouldRetry(current.AttemptCount))
        {
            Log.JobAbandoned(logger, current.OperationId, reason);

            // Persist per-attempt warnings to structured execution logs before
            // clearing them from the requeued record, so they remain observable.
            // Best-effort: a transient log-store failure must not block the
            // durable requeue/terminal transition.
            if (warnings is { Count: > 0 } && logStore != null)
            {
                try
                {
                    foreach (var warning in warnings)
                    {
                        await logStore.AppendAsync(current.OperationId, new ExecutionLogEntry
                        {
                            Timestamp = DateTimeOffset.UtcNow,
                            Level = ExecutionLogLevel.Warning,
                            Message = warning,
                            Phase = $"Attempt {current.AttemptCount} (requeuing)"
                        }, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Log.WarningLogAppendFailed(logger, current.OperationId, ex);
                }
            }

            var delay = forceRequeue ? TimeSpan.Zero : retryPolicy.ComputeDelay(current.AttemptCount + 1);
            var now = DateTimeOffset.UtcNow;
            var abandoned = current with
            {
                Status = ExecutionJobStatus.Queued,
                ClaimedBy = null,
                ClaimedAt = null,
                LastHeartbeatAt = null,
                UpdatedAt = now,
                CurrentPhase = $"Requeued: {reason}",
                PercentComplete = null,
                ErrorMessage = null,
                ProviderOperationId = null,
                CompletedAt = null,
                ArtifactReferences = Array.Empty<string>(),
                Warnings = Array.Empty<string>(),
                NextRetryAt = delay > TimeSpan.Zero ? now.Add(delay) : null
            };
            await jobStore.SetAsync(abandoned, cancellationToken: cancellationToken).ConfigureAwait(false);

            await jobQueue.RequeueAsync(
                current.OperationId,
                current.Priority,
                delay > TimeSpan.Zero ? delay : null,
                cancellationToken).ConfigureAwait(false);

            // Clear the tracked CTS immediately so that Cancel() returns false
            // for a job this worker no longer owns. Without this, a cancel
            // arriving between requeue and the ProcessJobAsync finally block
            // would be delegated to a worker that already dropped ownership,
            // causing the cancellation to be silently swallowed while the
            // retried job stays queued. The finally-block Remove is retained
            // as a no-op safety net.
            cancellationTokens.Remove(current.OperationId, workerId);
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            var failed = current with
            {
                Status = ExecutionJobStatus.Failed,
                UpdatedAt = now,
                CompletedAt = now,
                ErrorMessage = $"Job abandoned: {reason}",
                Warnings = warnings ?? current.Warnings,
                CurrentPhase = "Failed (abandoned)"
            };
            await jobStore.SetAsync(failed, cancellationToken: cancellationToken).ConfigureAwait(false);
            await jobQueue.RemoveAsync(current.OperationId, cancellationToken).ConfigureAwait(false);

            if (logStore != null)
            {
                await logStore.SetRetentionAsync(current.OperationId, LogRetention, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the job record is no longer in an active state owned
    /// by <paramref name="workerId"/>, indicating that the reconciler or another
    /// worker has already transitioned the job. Callers should skip their own
    /// state transition to avoid overwriting the authoritative update.
    /// </summary>
    private static bool IsTerminalOrNotOwnedBy(ExecutionJobRecord job, string workerId)
        => job.Status is not (ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running)
           || job.ClaimedBy != workerId;

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

    /// <summary>
    /// Builds a worker ID that always preserves the full GUID suffix for ownership
    /// uniqueness, truncating the machine-name prefix when necessary to stay within
    /// the 48-character budget.
    /// </summary>
    internal static string GenerateWorkerId()
        => GenerateWorkerId(Environment.MachineName, Guid.NewGuid());

    internal static string GenerateWorkerId(string machineName, Guid workerGuid)
    {
        var guid = workerGuid.ToString("N");
        var safeMachineName = string.IsNullOrWhiteSpace(machineName) ? "unknown" : machineName;
        var prefix = $"worker-{safeMachineName}";
        const int maxLength = 48;
        const int separatorLength = 1;
        var maxPrefixLength = maxLength - guid.Length - separatorLength;
        if (prefix.Length > maxPrefixLength)
        {
            prefix = prefix[..maxPrefixLength];
        }

        return $"{prefix}-{guid}";
    }

    private static partial class Log
    {
        [LoggerMessage(9050, LogLevel.Information, "Job execution worker started: {WorkerId}")]
        public static partial void WorkerStarted(ILogger logger, string workerId);

        [LoggerMessage(9051, LogLevel.Information, "Job execution worker stopped: {WorkerId}")]
        public static partial void WorkerStopped(ILogger logger, string workerId);

        [LoggerMessage(9052, LogLevel.Information, "Job execution started: {OperationId}, Kind={Kind}")]
        public static partial void JobExecutionStarted(ILogger logger, string operationId, string kind);

        [LoggerMessage(9053, LogLevel.Information, "Job execution completed: {OperationId}, Status={Status}")]
        public static partial void JobExecutionCompleted(ILogger logger, string operationId, string status);

        [LoggerMessage(9054, LogLevel.Error, "Job execution failed: {OperationId}")]
        public static partial void JobExecutionFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9055, LogLevel.Warning, "Job not found during execution: {OperationId}")]
        public static partial void JobNotFoundDuringExecution(ILogger logger, string operationId);

        [LoggerMessage(9056, LogLevel.Warning, "No executor registered for job kind: {OperationId}, Kind={Kind}")]
        public static partial void NoExecutorForKind(ILogger logger, string operationId, string kind);

        [LoggerMessage(9057, LogLevel.Warning, "Job abandoned: {OperationId}, Reason={Reason}")]
        public static partial void JobAbandoned(ILogger logger, string operationId, string reason);

        [LoggerMessage(9058, LogLevel.Error, "Claim loop error")]
        public static partial void ClaimLoopError(ILogger logger, Exception exception);

        [LoggerMessage(9059, LogLevel.Warning, "Job timed out: {OperationId}, MaxDuration={MaxDuration}")]
        public static partial void JobTimedOut(ILogger logger, string operationId, TimeSpan maxDuration);

        [LoggerMessage(9060, LogLevel.Information, "Job cancelled by operator: {OperationId}")]
        public static partial void JobCancelledByOperator(ILogger logger, string operationId);

        [LoggerMessage(9061, LogLevel.Warning, "No job executors registered for worker {WorkerId}; claim loop will not match any jobs")]
        public static partial void NoExecutorsRegistered(ILogger logger, string workerId);

        [LoggerMessage(9062, LogLevel.Warning, "Skipping state transition for job {OperationId}: current status is {Status} (reconciler or another worker intervened)")]
        public static partial void TerminalStateSkipped(ILogger logger, string operationId, string status);

        [LoggerMessage(9063, LogLevel.Warning, "Heartbeat pump faulted for job {OperationId}; finalization will proceed from executor outcome")]
        public static partial void HeartbeatPumpFaulted(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9065, LogLevel.Warning, "Failed to persist per-attempt warnings for job {OperationId}; requeue/terminal transition will proceed")]
        public static partial void WarningLogAppendFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9066, LogLevel.Error, "Failed to requeue job {OperationId} during pre-execution shutdown; stale-claim reconciliation will recover")]
        public static partial void PreExecShutdownCleanupFailed(ILogger logger, string operationId, Exception exception);
    }
}

/// <summary>
/// Execution context provided to <see cref="IJobExecutor"/> during job execution.
/// Mediates heartbeat pumping, progress reporting, log appending, and artifact publication.
/// Serializes read-modify-write operations on the job record to prevent concurrent
/// heartbeat, progress, and artifact writes from clobbering each other.
/// </summary>
internal sealed partial class JobExecutionContext(
    string operationId,
    string workerId,
    IExecutionJobStore jobStore,
    IExecutionLogStore? logStore,
    JobHeartbeatPolicy heartbeatPolicy,
    ILogger logger) : IJobExecutionContext, IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string OperationId => operationId;

    public async Task ReportProgressAsync(
        double? percentComplete,
        string? phase,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (job == null || !IsOwnedBy(job))
            {
                return;
            }

            var updated = job with
            {
                PercentComplete = percentComplete,
                CurrentPhase = phase ?? job.CurrentPhase,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastHeartbeatAt = DateTimeOffset.UtcNow
            };
            await jobStore.SetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task AppendLogAsync(
        ExecutionLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (logStore == null)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (job == null || !IsOwnedBy(job))
            {
                return;
            }

            await logStore.AppendAsync(operationId, entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task PublishArtifactAsync(
        string artifactReference,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (job == null || !IsOwnedBy(job))
            {
                return;
            }

            var refs = new List<string>(job.ArtifactReferences) { artifactReference };
            var updated = job with
            {
                ArtifactReferences = refs,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastHeartbeatAt = DateTimeOffset.UtcNow
            };
            await jobStore.SetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Pumps heartbeat signals at the configured interval until cancelled.
    /// </summary>
    internal async Task RunHeartbeatPumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(heartbeatPolicy.Interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
                    if (job == null)
                    {
                        break;
                    }

                    // Stop pumping if the reconciler already marked this job terminal
                    // or ownership has moved to another worker.
                    if (!IsOwnedBy(job))
                    {
                        break;
                    }

                    var updated = job with
                    {
                        LastHeartbeatAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await jobStore.SetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.HeartbeatWriteFailed(logger, operationId, ex);
            }
        }
    }

    private bool IsOwnedBy(ExecutionJobRecord job)
        => job.Status is ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running
           && job.ClaimedBy == workerId;

    private static partial class Log
    {
        [LoggerMessage(9064, LogLevel.Warning, "Heartbeat write failed for job {OperationId}; pump will retry on next interval")]
        public static partial void HeartbeatWriteFailed(ILogger logger, string operationId, Exception exception);
    }

    public void Dispose() => _writeLock.Dispose();
}

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
    IExecutionLogStore? logStore,
    ILogger<JobExecutionService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);

    private readonly Dictionary<ExecutionJobKind, IJobExecutor> _executorMap =
        executors.ToDictionary(e => e.Kind);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = $"worker-{Environment.MachineName}-{Guid.NewGuid():N}"[..48];
        Log.WorkerStarted(logger, workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimedId = await jobQueue.TryClaimAsync(
                    workerId, cancellationToken: stoppingToken).ConfigureAwait(false);

                if (claimedId != null)
                {
                    await ProcessJobAsync(claimedId, workerId, stoppingToken).ConfigureAwait(false);
                    continue; // Try to claim the next job immediately.
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
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
            await AbandonJobAsync(job, "No executor registered for job kind.", stoppingToken).ConfigureAwait(false);
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

        // Create cancellation that combines host stopping and per-job cancellation.
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // Set up timeout if configured.
        var timeoutPolicy = job.TimeoutPolicy ?? JobTimeoutPolicy.Default;
        jobCts.CancelAfter(timeoutPolicy.MaxDuration);

        // Create execution context with heartbeat pump.
        using var context = new JobExecutionContext(
            operationId, jobStore, logStore, job.HeartbeatPolicy ?? JobHeartbeatPolicy.Default);

        // Start heartbeat pump in background.
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeatTask = context.RunHeartbeatPumpAsync(heartbeatCts.Token);

        try
        {
            var result = await executor.ExecuteAsync(running, context, jobCts.Token).ConfigureAwait(false);
            await heartbeatCts.CancelAsync().ConfigureAwait(false);

            if (result.Status == ExecutionJobStatus.Succeeded)
            {
                await FinalizeJobAsync(operationId, result, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                // Executor returned failure — route through retry policy.
                await AbandonJobAsync(running, result.ErrorMessage ?? "Execution failed.", stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await heartbeatCts.CancelAsync().ConfigureAwait(false);
            // Worker is shutting down; abandon the job so it can be retried.
            await AbandonJobAsync(running, "Worker shutdown.", stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await heartbeatCts.CancelAsync().ConfigureAwait(false);
            // Timeout or cancellation — route through retry policy.
            await AbandonJobAsync(running, "Job was cancelled or timed out.", stoppingToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await heartbeatCts.CancelAsync().ConfigureAwait(false);
            Log.JobExecutionFailed(logger, operationId, ex);
            // Execution exception — route through retry policy.
            await AbandonJobAsync(running, ex.Message, stoppingToken).ConfigureAwait(false);
        }

        // Ensure heartbeat pump stops cleanly.
        try
        {
            await heartbeatTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    private async Task FinalizeJobAsync(
        string operationId,
        JobExecutionResult result,
        CancellationToken cancellationToken)
    {
        var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
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

    private async Task AbandonJobAsync(
        ExecutionJobRecord job,
        string reason,
        CancellationToken cancellationToken)
    {
        // Re-read to capture progress and artifact updates made during execution.
        var current = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false) ?? job;
        var retryPolicy = current.RetryPolicy ?? JobRetryPolicy.Default;

        if (retryPolicy.ShouldRetry(current.AttemptCount))
        {
            Log.JobAbandoned(logger, current.OperationId, reason);

            var abandoned = current with
            {
                Status = ExecutionJobStatus.Queued,
                ClaimedBy = null,
                ClaimedAt = null,
                LastHeartbeatAt = null,
                UpdatedAt = DateTimeOffset.UtcNow,
                CurrentPhase = $"Requeued: {reason}"
            };
            await jobStore.SetAsync(abandoned, cancellationToken: cancellationToken).ConfigureAwait(false);

            var delay = retryPolicy.ComputeDelay(current.AttemptCount + 1);
            await jobQueue.RequeueAsync(
                current.OperationId,
                current.Priority,
                delay > TimeSpan.Zero ? delay : null,
                cancellationToken).ConfigureAwait(false);
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
                CurrentPhase = "Failed (abandoned)"
            };
            await jobStore.SetAsync(failed, cancellationToken: cancellationToken).ConfigureAwait(false);
            await jobQueue.RemoveAsync(current.OperationId, cancellationToken).ConfigureAwait(false);
        }
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
    }
}

/// <summary>
/// Execution context provided to <see cref="IJobExecutor"/> during job execution.
/// Mediates heartbeat pumping, progress reporting, log appending, and artifact publication.
/// Serializes read-modify-write operations on the job record to prevent concurrent
/// heartbeat, progress, and artifact writes from clobbering each other.
/// </summary>
internal sealed class JobExecutionContext(
    string operationId,
    IExecutionJobStore jobStore,
    IExecutionLogStore? logStore,
    JobHeartbeatPolicy heartbeatPolicy) : IJobExecutionContext, IDisposable
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
            if (job == null)
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
        if (logStore != null)
        {
            await logStore.AppendAsync(operationId, entry, cancellationToken).ConfigureAwait(false);
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
            if (job == null)
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
        }
    }

    public void Dispose() => _writeLock.Dispose();
}

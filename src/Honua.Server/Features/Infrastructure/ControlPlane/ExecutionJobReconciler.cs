// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Reconciles durable execution-job records against pluggable batch-compute backends.
/// Mirrors <see cref="DeployWorkflowReconciler"/> for the run-to-completion job lifecycle
/// so that optional distributed executors plug in behind the canonical runtime.
/// </summary>
internal sealed partial class ExecutionJobReconciler(
    IExecutionJobStore jobStore,
    IEnumerable<IBatchComputeBackend> backends,
    IUniversalProgressStore progressStore,
    ILogger<ExecutionJobReconciler> logger) : IExecutionJobReconciler
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromDays(7);

    private readonly IReadOnlyList<IBatchComputeBackend> _backends = backends.ToArray();
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public async Task ReconcileExecutionJobAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        var leaseAcquired = await jobStore
            .TryAcquireLeaseAsync(operationId, _ownerId, LeaseDuration, cancellationToken)
            .ConfigureAwait(false);
        if (!leaseAcquired)
        {
            return;
        }

        using var reconciliationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalTask = RenewLeaseUntilCancelledAsync(operationId, reconciliationCancellation);

        try
        {
            var job = await jobStore.GetAsync(operationId, reconciliationCancellation.Token).ConfigureAwait(false);
            if (job == null || IsTerminal(job.Status))
            {
                return;
            }

            var backend = _backends.Resolve(job.Spec.Backend, job.Spec.TargetKind);
            if (backend == null)
            {
                Log.BackendMissing(logger, operationId, job.Spec.Backend, job.Spec.TargetKind.ToString());
                var failedAt = DateTimeOffset.UtcNow;
                var missing = job with
                {
                    Status = ExecutionJobStatus.Failed,
                    UpdatedAt = failedAt,
                    CompletedAt = failedAt,
                    CurrentPhase = "No batch compute backend is registered for this job.",
                    ErrorMessage = $"No batch compute backend registered for '{job.Spec.Backend}' ({job.Spec.TargetKind})."
                };
                if (await jobStore.TrySetAsync(missing, cancellationToken: reconciliationCancellation.Token).ConfigureAwait(false))
                {
                    await BridgeProgressAsync(job, missing, reconciliationCancellation.Token).ConfigureAwait(false);
                }

                return;
            }

            var updated = job.Status switch
            {
                ExecutionJobStatus.Queued when string.Equals(job.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
                    && job.CancellationRequestedAt.HasValue
                    => await CancelJobAsync(job, backend, reconciliationCancellation.Token).ConfigureAwait(false),
                ExecutionJobStatus.Queued when string.Equals(job.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
                    => await ObserveJobAsync(job, backend, reconciliationCancellation.Token).ConfigureAwait(false),
                ExecutionJobStatus.Queued when job.CancellationRequestedAt.HasValue
                    && (!string.IsNullOrEmpty(job.ProviderOperationId) || job.AttemptCount > 0)
                    => await CancelJobAsync(job, backend, reconciliationCancellation.Token).ConfigureAwait(false),
                ExecutionJobStatus.Queued when !string.IsNullOrEmpty(job.ProviderOperationId) || job.AttemptCount > 0
                    => await ObserveJobAsync(job, backend, reconciliationCancellation.Token).ConfigureAwait(false),
                ExecutionJobStatus.Queued when job.CancellationRequestedAt.HasValue
                    => CancelQueuedJob(job),
                ExecutionJobStatus.Queued => await StartJobAsync(job, backend, reconciliationCancellation.Token).ConfigureAwait(false),
                ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running when job.CancellationRequestedAt.HasValue
                    => await CancelJobAsync(job, backend, reconciliationCancellation.Token).ConfigureAwait(false),
                ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running
                    => await ObserveJobAsync(job, backend, reconciliationCancellation.Token).ConfigureAwait(false),
                _ => job
            };

            if (!ReferenceEquals(updated, job))
            {
                var persisted = await jobStore.TrySetAsync(updated, cancellationToken: reconciliationCancellation.Token).ConfigureAwait(false);
                if (!persisted)
                {
                    Log.ExecutionJobCasConflict(logger, operationId);
                    return;
                }

                await BridgeProgressAsync(job, updated, reconciliationCancellation.Token).ConfigureAwait(false);
                Log.ExecutionJobReconciled(logger, operationId, updated.Status.ToString(), updated.PercentComplete ?? double.NaN);
            }
        }
        catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Log.ExecutionJobLeaseLost(logger, operationId);
            return;
        }
        catch (Exception ex)
        {
            var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (job != null && !IsTerminal(job.Status))
            {
                var failedAt = DateTimeOffset.UtcNow;
                var failed = job with
                {
                    Status = ExecutionJobStatus.Failed,
                    UpdatedAt = failedAt,
                    CompletedAt = failedAt,
                    CurrentPhase = "Execution-job reconciliation failed.",
                    ErrorMessage = $"Execution-job reconciliation failed due to {ex.GetType().Name}."
                };
                if (await jobStore.TrySetAsync(failed, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    await BridgeProgressAsync(job, failed, cancellationToken).ConfigureAwait(false);
                }
            }

            throw;
        }
        finally
        {
            reconciliationCancellation.Cancel();
            try
            {
                await renewalTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested)
            {
            }

            await jobStore.ReleaseLeaseAsync(operationId, _ownerId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ExecutionJobRecord CancelQueuedJob(ExecutionJobRecord job)
    {
        var now = DateTimeOffset.UtcNow;
        return job with
        {
            Status = ExecutionJobStatus.Cancelled,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = "Cancelled before submission"
        };
    }

    private static async Task<ExecutionJobRecord> StartJobAsync(
        ExecutionJobRecord job,
        IBatchComputeBackend backend,
        CancellationToken cancellationToken)
    {
        var submission = await backend.StartAsync(job, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return job with
        {
            Status = submission.Status,
            UpdatedAt = now,
            CompletedAt = IsTerminal(submission.Status) ? now : job.CompletedAt,
            ProviderOperationId = submission.ProviderOperationId ?? job.ProviderOperationId,
            CurrentPhase = submission.Message ?? job.CurrentPhase,
            AttemptCount = job.AttemptCount + 1
        };
    }

    private static async Task<ExecutionJobRecord> CancelJobAsync(
        ExecutionJobRecord job,
        IBatchComputeBackend backend,
        CancellationToken cancellationToken)
    {
        var capabilities = await backend.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.SupportsCancellation)
        {
            return await ObserveJobAsync(job, backend, cancellationToken).ConfigureAwait(false);
        }

        var observation = await backend.CancelAsync(job, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return job with
        {
            Status = observation.Status,
            UpdatedAt = now,
            CompletedAt = IsTerminal(observation.Status) ? now : job.CompletedAt,
            ProviderOperationId = observation.ProviderOperationId ?? job.ProviderOperationId,
            PercentComplete = observation.PercentComplete ?? job.PercentComplete,
            CurrentPhase = observation.Message ?? job.CurrentPhase,
            ErrorMessage = observation.Status == ExecutionJobStatus.Failed
                ? observation.Message ?? job.ErrorMessage
                : job.ErrorMessage
        };
    }

    private static async Task<ExecutionJobRecord> ObserveJobAsync(
        ExecutionJobRecord job,
        IBatchComputeBackend backend,
        CancellationToken cancellationToken)
    {
        var observation = await backend.ObserveAsync(job, cancellationToken).ConfigureAwait(false);

        var newProviderOpId = observation.ProviderOperationId ?? job.ProviderOperationId;
        var newPercent = observation.PercentComplete ?? job.PercentComplete;
        var newPhase = observation.Message ?? job.CurrentPhase;
        var newError = observation.Status == ExecutionJobStatus.Failed
            ? observation.Message ?? job.ErrorMessage
            : job.ErrorMessage;

        if (observation.Status == job.Status &&
            string.Equals(newProviderOpId, job.ProviderOperationId, StringComparison.Ordinal) &&
            Math.Abs((newPercent ?? 0d) - (job.PercentComplete ?? 0d)) < 0.0001 &&
            string.Equals(newPhase, job.CurrentPhase, StringComparison.Ordinal) &&
            string.Equals(newError, job.ErrorMessage, StringComparison.Ordinal))
        {
            return job;
        }

        var now = DateTimeOffset.UtcNow;
        return job with
        {
            Status = observation.Status,
            UpdatedAt = now,
            CompletedAt = IsTerminal(observation.Status) ? now : job.CompletedAt,
            ProviderOperationId = newProviderOpId,
            PercentComplete = newPercent,
            CurrentPhase = newPhase,
            ErrorMessage = newError
        };
    }

    private async Task BridgeProgressAsync(
        ExecutionJobRecord previous,
        ExecutionJobRecord updated,
        CancellationToken cancellationToken)
    {
        if (Math.Abs((previous.PercentComplete ?? 0d) - (updated.PercentComplete ?? 0d)) < 0.0001 &&
            string.Equals(previous.CurrentPhase, updated.CurrentPhase, StringComparison.Ordinal) &&
            previous.Status == updated.Status)
        {
            return;
        }

        try
        {
            var existing = await progressStore
                .GetProgressAsync<GeoprocessingProgress>(updated.OperationId, cancellationToken)
                .ConfigureAwait(false);

            var bridged = BuildProgress(updated, existing);
            if (bridged != null)
            {
                await progressStore
                    .SetProgressAsync(updated.OperationId, bridged, ProgressRetention, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.ExecutionJobProgressBridgeFailed(logger, updated.OperationId, ex);
        }
    }

    internal static GeoprocessingProgress? BuildProgress(ExecutionJobRecord job, GeoprocessingProgress? existing)
    {
        if (existing == null && job.Spec.Kind != ExecutionJobKind.Geoprocessing)
        {
            return null;
        }

        var workflowStatus = MapWorkflowStatus(job.Status);
        var stageStatus = MapStageStatus(job.Status);
        var (stepsCompleted, totalSteps) = ResolveSyntheticSteps(existing, job.PercentComplete);
        var planId = existing?.PlanId
            ?? job.Spec.Parameters.GetValueOrDefault(ExecutionJobParameterKeys.GeoprocessingPlanId)
            ?? job.Spec.WorkloadId;

        if (existing != null)
        {
            return existing with
            {
                WorkflowStatus = workflowStatus,
                CurrentStageStatus = stageStatus,
                StepsCompleted = stepsCompleted,
                TotalSteps = totalSteps,
                CurrentPhase = job.CurrentPhase ?? existing.CurrentPhase,
                ErrorMessage = job.ErrorMessage ?? existing.ErrorMessage,
                CompletedAt = job.CompletedAt ?? existing.CompletedAt
            };
        }

        return new GeoprocessingProgress
        {
            OperationId = job.OperationId,
            WorkflowStatus = workflowStatus,
            CurrentStage = GeoprocessingStageKind.Execute,
            CurrentStageStatus = stageStatus,
            PlanId = planId,
            StepsCompleted = stepsCompleted,
            TotalSteps = totalSteps,
            StartedAt = job.CreatedAt,
            CompletedAt = job.CompletedAt,
            CurrentPhase = job.CurrentPhase,
            ErrorMessage = job.ErrorMessage
        };
    }

    internal static (int StepsCompleted, int? TotalSteps) ResolveSyntheticSteps(
        GeoprocessingProgress? existing,
        double? percentComplete)
    {
        if (!percentComplete.HasValue)
        {
            return (existing?.StepsCompleted ?? 0, existing?.TotalSteps);
        }

        var clampedPercent = Math.Clamp(percentComplete.Value, 0d, 100d);
        var totalSteps = existing?.TotalSteps is > 0
            ? existing.TotalSteps
            : 100;
        var stepsCompleted = (int)Math.Round(clampedPercent / 100d * totalSteps.Value, MidpointRounding.AwayFromZero);
        stepsCompleted = Math.Clamp(stepsCompleted, 0, totalSteps.Value);
        return (stepsCompleted, totalSteps);
    }

    internal static GeoprocessingWorkflowStatus MapWorkflowStatus(ExecutionJobStatus status)
        => status switch
        {
            ExecutionJobStatus.Queued => GeoprocessingWorkflowStatus.AwaitingExecution,
            ExecutionJobStatus.Provisioning => GeoprocessingWorkflowStatus.AwaitingExecution,
            ExecutionJobStatus.Running => GeoprocessingWorkflowStatus.Running,
            ExecutionJobStatus.Succeeded => GeoprocessingWorkflowStatus.Completed,
            ExecutionJobStatus.Failed => GeoprocessingWorkflowStatus.Failed,
            ExecutionJobStatus.Cancelled => GeoprocessingWorkflowStatus.Cancelled,
            _ => GeoprocessingWorkflowStatus.AwaitingExecution
        };

    internal static GeoprocessingStageStatus MapStageStatus(ExecutionJobStatus status)
        => status switch
        {
            ExecutionJobStatus.Queued => GeoprocessingStageStatus.Pending,
            ExecutionJobStatus.Provisioning => GeoprocessingStageStatus.Pending,
            ExecutionJobStatus.Running => GeoprocessingStageStatus.Pending,
            ExecutionJobStatus.Succeeded => GeoprocessingStageStatus.Completed,
            ExecutionJobStatus.Failed => GeoprocessingStageStatus.Failed,
            ExecutionJobStatus.Cancelled => GeoprocessingStageStatus.Cancelled,
            _ => GeoprocessingStageStatus.Pending
        };

    private async Task RenewLeaseUntilCancelledAsync(
        string operationId,
        CancellationTokenSource reconciliationCancellation)
    {
        while (!reconciliationCancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LeaseRenewInterval, reconciliationCancellation.Token).ConfigureAwait(false);
                var renewed = await jobStore.RenewLeaseAsync(
                    operationId,
                    _ownerId,
                    LeaseDuration,
                    reconciliationCancellation.Token).ConfigureAwait(false);
                if (!renewed)
                {
                    reconciliationCancellation.Cancel();
                    return;
                }
            }
            catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

    internal static partial class Log
    {
        [LoggerMessage(9030, LogLevel.Debug, "Reconciled execution job {OperationId} to status {Status} ({PercentComplete}%)")]
        public static partial void ExecutionJobReconciled(ILogger logger, string operationId, string status, double percentComplete);

        [LoggerMessage(9031, LogLevel.Warning, "Execution-job reconciliation failed for operation {OperationId}")]
        public static partial void ExecutionJobReconcileFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9032, LogLevel.Warning, "Execution-job reconciliation poll loop failed")]
        public static partial void ExecutionJobPollLoopFailed(ILogger logger, Exception exception);

        [LoggerMessage(9033, LogLevel.Debug, "Execution-job reconciliation lease was lost for operation {OperationId}; another node may continue processing.")]
        public static partial void ExecutionJobLeaseLost(ILogger logger, string operationId);

        [LoggerMessage(9034, LogLevel.Warning, "No batch compute backend registered for execution job {OperationId} ({Backend}/{TargetKind})")]
        public static partial void BackendMissing(ILogger logger, string operationId, string backend, string targetKind);

        [LoggerMessage(9035, LogLevel.Warning, "Failed to bridge execution-job progress for {OperationId}")]
        public static partial void ExecutionJobProgressBridgeFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9037, LogLevel.Debug, "Execution-job reconciliation CAS conflict for {OperationId}; deferring to next sweep")]
        public static partial void ExecutionJobCasConflict(ILogger logger, string operationId);
    }

    internal static partial class BackgroundLog
    {
        [LoggerMessage(9036, LogLevel.Information, "Started execution-job reconciliation background service")]
        public static partial void BackgroundServiceStarted(ILogger logger);
    }
}

/// <summary>
/// Background worker that continuously reconciles active execution jobs against their batch-compute backend.
/// </summary>
internal sealed class ExecutionJobReconcilerBackgroundService(
    IExecutionJobStore jobStore,
    IExecutionJobReconciler reconciler,
    ILogger<ExecutionJobReconcilerBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ExecutionJobReconciler.BackgroundLog.BackgroundServiceStarted(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var activeJobs = await jobStore.ListActiveAsync(kind: null, stoppingToken).ConfigureAwait(false);
                foreach (var job in activeJobs)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        await reconciler.ReconcileExecutionJobAsync(job.OperationId, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        ExecutionJobReconciler.Log.ExecutionJobReconcileFailed(logger, job.OperationId, ex);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ExecutionJobReconciler.Log.ExecutionJobPollLoopFailed(logger, ex);
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

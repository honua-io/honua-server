// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Reconciles durable execution job records against batch-compute backend state.
/// </summary>
internal sealed partial class ExecutionJobReconciler(
    IExecutionJobStore executionJobStore,
    IEnumerable<IBatchComputeBackend> backends,
    ILogger<ExecutionJobReconciler> logger)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(10);

    private readonly Dictionary<(string Backend, BatchComputeTargetKind TargetKind), IBatchComputeBackend> _backends = backends.ToDictionary(
        backend => (backend.BackendName, backend.TargetKind),
        backend => backend,
        EqualityComparer<(string Backend, BatchComputeTargetKind TargetKind)>.Default);
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public async Task ReconcileAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        var leaseAcquired = await executionJobStore
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
            var job = await executionJobStore.GetAsync(operationId, reconciliationCancellation.Token).ConfigureAwait(false);
            if (job == null || IsTerminal(job.Status))
            {
                return;
            }

            if (!_backends.TryGetValue((job.Spec.Backend, job.Spec.TargetKind), out var backend))
            {
                var failedAt = DateTimeOffset.UtcNow;
                var failedJob = job with
                {
                    Status = ExecutionJobStatus.Failed,
                    UpdatedAt = failedAt,
                    CompletedAt = failedAt,
                    CurrentPhase = "Execution job reconciliation requires a registered batch-compute backend.",
                    ErrorMessage = $"No batch-compute backend is registered for '{job.Spec.Backend}' ({job.Spec.TargetKind})."
                };

                await executionJobStore.SetAsync(failedJob, cancellationToken: reconciliationCancellation.Token).ConfigureAwait(false);
                Log.ExecutionJobBackendMissing(logger, operationId, job.Spec.Backend, job.Spec.TargetKind.ToString());
                return;
            }

            ExecutionJobRecord updated;
            if (string.IsNullOrWhiteSpace(job.ProviderOperationId))
            {
                var submission = await backend.StartAsync(job, reconciliationCancellation.Token).ConfigureAwait(false);
                var submittedAt = DateTimeOffset.UtcNow;
                updated = job with
                {
                    Status = submission.Status,
                    UpdatedAt = submittedAt,
                    CompletedAt = IsTerminal(submission.Status) ? submittedAt : job.CompletedAt,
                    ProviderOperationId = submission.ProviderOperationId ?? job.ProviderOperationId,
                    CurrentPhase = submission.Message ?? job.CurrentPhase,
                    ErrorMessage = submission.Status == ExecutionJobStatus.Failed
                        ? submission.Message ?? job.ErrorMessage
                        : job.ErrorMessage
                };
            }
            else
            {
                var observation = await backend.ObserveAsync(job, reconciliationCancellation.Token).ConfigureAwait(false);
                var updatedAt = DateTimeOffset.UtcNow;
                updated = job with
                {
                    Status = observation.Status,
                    UpdatedAt = updatedAt,
                    CompletedAt = IsTerminal(observation.Status) ? updatedAt : job.CompletedAt,
                    ProviderOperationId = observation.ProviderOperationId ?? job.ProviderOperationId,
                    PercentComplete = observation.PercentComplete ?? job.PercentComplete,
                    CurrentPhase = observation.Message ?? job.CurrentPhase,
                    ErrorMessage = observation.Status == ExecutionJobStatus.Failed
                        ? observation.Message ?? job.ErrorMessage
                        : job.ErrorMessage
                };
            }

            if (!Equals(updated, job))
            {
                await executionJobStore.SetAsync(updated, cancellationToken: reconciliationCancellation.Token).ConfigureAwait(false);
                Log.ExecutionJobReconciled(logger, operationId, updated.Status.ToString());
            }
        }
        catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Log.ExecutionJobLeaseLost(logger, operationId);
            return;
        }
        catch (Exception ex)
        {
            var job = await executionJobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (job != null && !IsTerminal(job.Status))
            {
                var failedAt = DateTimeOffset.UtcNow;
                var failedJob = job with
                {
                    Status = ExecutionJobStatus.Failed,
                    UpdatedAt = failedAt,
                    CompletedAt = failedAt,
                    CurrentPhase = "Execution job reconciliation failed.",
                    ErrorMessage = $"Execution job reconciliation failed due to {ex.GetType().Name}: {ex.Message}"
                };

                await executionJobStore.SetAsync(failedJob, cancellationToken: cancellationToken).ConfigureAwait(false);
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

            await executionJobStore.ReleaseLeaseAsync(operationId, _ownerId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RenewLeaseUntilCancelledAsync(string operationId, CancellationTokenSource reconciliationCancellation)
    {
        while (!reconciliationCancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LeaseRenewInterval, reconciliationCancellation.Token).ConfigureAwait(false);
                var renewed = await executionJobStore.RenewLeaseAsync(
                        operationId,
                        _ownerId,
                        LeaseDuration,
                        reconciliationCancellation.Token)
                    .ConfigureAwait(false);
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

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

    internal static partial class Log
    {
        [LoggerMessage(9050, LogLevel.Debug, "Reconciled execution job {OperationId} to status {Status}")]
        public static partial void ExecutionJobReconciled(ILogger logger, string operationId, string status);

        [LoggerMessage(9051, LogLevel.Warning, "Execution job reconciliation failed for {OperationId}")]
        public static partial void ExecutionJobReconcileFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9052, LogLevel.Warning, "Execution job reconciliation poll loop failed")]
        public static partial void ExecutionJobPollLoopFailed(ILogger logger, Exception exception);

        [LoggerMessage(9053, LogLevel.Debug, "Execution job reconciliation lease was lost for {OperationId}; another node may continue processing.")]
        public static partial void ExecutionJobLeaseLost(ILogger logger, string operationId);

        [LoggerMessage(9054, LogLevel.Warning, "Execution job {OperationId} could not find a registered backend {Backend} ({TargetKind}); marked failed.")]
        public static partial void ExecutionJobBackendMissing(ILogger logger, string operationId, string backend, string targetKind);
    }

    internal static partial class BackgroundLog
    {
        [LoggerMessage(9055, LogLevel.Information, "Started execution job reconciliation background service")]
        public static partial void BackgroundServiceStarted(ILogger logger);
    }
}

/// <summary>
/// Background worker that continuously reconciles active execution jobs.
/// </summary>
internal sealed class ExecutionJobReconcilerBackgroundService(
    IExecutionJobStore executionJobStore,
    ExecutionJobReconciler reconciler,
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
                var activeJobs = await executionJobStore.ListActiveAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                foreach (var job in activeJobs)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        await reconciler.ReconcileAsync(job.OperationId, stoppingToken).ConfigureAwait(false);
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

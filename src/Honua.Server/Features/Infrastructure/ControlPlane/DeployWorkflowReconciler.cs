// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Reconciles durable deploy workflow operations against provider backends.
/// </summary>
internal sealed partial class DeployWorkflowReconciler(
    IWorkflowOperationStore workflowStore,
    IDeployTargetRegistry targetRegistry,
    IEnumerable<IDeployBackend> backends,
    IDeployTelemetrySignalEvaluator telemetrySignalEvaluator,
    ILogger<DeployWorkflowReconciler> logger) : IOperationReconciler
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private readonly Dictionary<(string Backend, DeployTargetKind TargetKind), IDeployBackend> _backends = backends.ToDictionary(
        backend => (backend.BackendName, backend.TargetKind),
        backend => backend,
        EqualityComparer<(string Backend, DeployTargetKind TargetKind)>.Default);
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public async Task ReconcileWorkflowOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        var leaseAcquired = await workflowStore.TryAcquireLeaseAsync(operationId, _ownerId, LeaseDuration, cancellationToken).ConfigureAwait(false);
        if (!leaseAcquired)
        {
            return;
        }

        try
        {
            var operation = await workflowStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (operation == null ||
                operation.Kind != WorkflowOperationKind.Deploy ||
                operation.Deploy == null ||
                IsTerminal(operation.Status) ||
                operation.Status is not (WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Reconciling or WorkflowOperationStatus.RollbackRequested))
            {
                return;
            }

            var target = await targetRegistry.GetAsync(operation.Deploy.TargetId, cancellationToken).ConfigureAwait(false);
            WorkflowOperationRecord updated;

            if (target == null || !_backends.TryGetValue((target.Backend, target.TargetKind), out var backend))
            {
                updated = operation with
                {
                    Status = WorkflowOperationStatus.ManualInterventionRequired,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Deploy reconciliation requires manual intervention because no backend is registered for this target.",
                    ErrorMessage = $"No deploy backend is registered for target '{operation.Deploy.TargetId}' ({operation.Deploy.Backend})."
                };
            }
            else
            {
                var observation = await backend.ObserveAsync(operation, cancellationToken).ConfigureAwait(false);
                updated = operation with
                {
                    Status = observation.Status,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CompletedAt = IsTerminal(observation.Status) ? DateTimeOffset.UtcNow : null,
                    ProviderOperationId = observation.ProviderOperationId ?? operation.ProviderOperationId,
                    CurrentPhase = observation.Message ?? operation.CurrentPhase,
                    ObservedState = observation.ObservedRevision ?? operation.ObservedState,
                    ErrorMessage = observation.Status == WorkflowOperationStatus.Failed
                        ? observation.Message ?? operation.ErrorMessage
                        : null,
                    Deploy = operation.Deploy with
                    {
                        CurrentRevision = string.IsNullOrWhiteSpace(operation.Deploy.CurrentRevision)
                            ? observation.ObservedRevision ?? operation.Deploy.CurrentRevision
                            : operation.Deploy.CurrentRevision
                    }
                };

                updated = await ApplyRollbackSignalsAsync(
                        operation,
                        updated,
                        backend,
                        observation.PromotionRecommended,
                        observation.RollbackRecommended,
                        observation.Message,
                        telemetrySignalEvaluator,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!Equals(updated, operation))
            {
                await workflowStore.SetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
                Log.WorkflowOperationReconciled(logger, operationId, updated.Status.ToString());
            }
        }
        catch (Exception ex)
        {
            var operation = await workflowStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (operation is
                {
                    Kind: WorkflowOperationKind.Deploy,
                    Deploy: not null
                } &&
                !IsTerminal(operation.Status))
            {
                var failedAt = DateTimeOffset.UtcNow;
                var failedOperation = operation with
                {
                    Status = WorkflowOperationStatus.ManualInterventionRequired,
                    UpdatedAt = failedAt,
                    CompletedAt = failedAt,
                    CurrentPhase = "Deploy reconciliation failed and requires manual intervention.",
                    ErrorMessage = $"Deploy reconciliation failed due to {ex.GetType().Name}."
                };

                await workflowStore.SetAsync(failedOperation, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            await workflowStore.ReleaseLeaseAsync(operationId, _ownerId, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ReconcileExecutionJobAsync(
        string operationId,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private static bool IsTerminal(WorkflowOperationStatus status)
        => status is WorkflowOperationStatus.Succeeded
            or WorkflowOperationStatus.Failed
            or WorkflowOperationStatus.RolledBack
            or WorkflowOperationStatus.ManualInterventionRequired;

    private static async Task<WorkflowOperationRecord> ApplyRollbackSignalsAsync(
        WorkflowOperationRecord previous,
        WorkflowOperationRecord current,
        IDeployBackend backend,
        bool promotionRecommended,
        bool backendRollbackRecommended,
        string? backendMessage,
        IDeployTelemetrySignalEvaluator telemetrySignalEvaluator,
        CancellationToken cancellationToken)
    {
        var rollbackReason = backendRollbackRecommended
            ? backendMessage ?? "Deploy backend recommended rollback."
            : null;

        if (current.Status is WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Reconciling or WorkflowOperationStatus.Succeeded)
        {
            var telemetryDecision = await telemetrySignalEvaluator.EvaluateAsync(current, cancellationToken).ConfigureAwait(false);
            if (telemetryDecision != null)
            {
                if (telemetryDecision.RollbackRecommended)
                {
                    rollbackReason = telemetryDecision.Message;
                }
                else if (telemetryDecision.WaitForMoreTelemetry)
                {
                    current = current with
                    {
                        Status = WorkflowOperationStatus.Reconciling,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        CompletedAt = null,
                        CurrentPhase = telemetryDecision.Message,
                        ErrorMessage = null
                    };
                }
                else if (!string.IsNullOrWhiteSpace(telemetryDecision.Message))
                {
                    current = current with
                    {
                        CurrentPhase = telemetryDecision.Message,
                        ErrorMessage = current.Status == WorkflowOperationStatus.Failed ? telemetryDecision.Message : current.ErrorMessage
                    };
                }

                if (!telemetryDecision.RollbackRecommended &&
                    !telemetryDecision.WaitForMoreTelemetry &&
                    promotionRecommended &&
                    current.Status == WorkflowOperationStatus.Reconciling)
                {
                    var promotionObservation = await backend.PromoteAsync(current, cancellationToken).ConfigureAwait(false);
                    current = current with
                    {
                        Status = promotionObservation.Status,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        CompletedAt = IsTerminal(promotionObservation.Status) ? DateTimeOffset.UtcNow : null,
                        ProviderOperationId = promotionObservation.ProviderOperationId ?? current.ProviderOperationId,
                        CurrentPhase = promotionObservation.Message ?? current.CurrentPhase,
                        ObservedState = promotionObservation.ObservedRevision ?? current.ObservedState,
                        ErrorMessage = promotionObservation.Status == WorkflowOperationStatus.Failed
                            ? promotionObservation.Message ?? current.ErrorMessage
                            : current.ErrorMessage
                    };
                }
            }
        }

        if (string.IsNullOrWhiteSpace(rollbackReason) ||
            current.Status is WorkflowOperationStatus.RollbackRequested or WorkflowOperationStatus.RolledBack)
        {
            return current;
        }

        var deploySpec = current.Deploy;
        if (deploySpec == null)
        {
            return current;
        }

        var rollbackObservation = await backend.RollbackAsync(current, cancellationToken).ConfigureAwait(false);
        var updatedAt = DateTimeOffset.UtcNow;
        return current with
        {
            Status = rollbackObservation.Status,
            UpdatedAt = updatedAt,
            CompletedAt = IsTerminal(rollbackObservation.Status) ? updatedAt : null,
            ProviderOperationId = rollbackObservation.ProviderOperationId ?? current.ProviderOperationId,
            CurrentPhase = rollbackReason,
            ObservedState = rollbackObservation.ObservedRevision ?? current.ObservedState,
            ErrorMessage = rollbackReason,
            Deploy = deploySpec with
            {
                CurrentRevision = string.IsNullOrWhiteSpace(deploySpec.CurrentRevision)
                    ? rollbackObservation.ObservedRevision ?? deploySpec.CurrentRevision
                    : deploySpec.CurrentRevision
            }
        };
    }

    internal static partial class Log
    {
        [LoggerMessage(9022, LogLevel.Debug, "Reconciled deploy workflow operation {OperationId} to status {Status}")]
        public static partial void WorkflowOperationReconciled(ILogger logger, string operationId, string status);

        [LoggerMessage(9023, LogLevel.Warning, "Deploy workflow reconciliation failed for operation {OperationId}")]
        public static partial void WorkflowOperationReconcileFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9024, LogLevel.Warning, "Deploy workflow reconciliation poll loop failed")]
        public static partial void WorkflowOperationPollLoopFailed(ILogger logger, Exception exception);
    }

    internal static partial class BackgroundLog
    {
        [LoggerMessage(9025, LogLevel.Information, "Started deploy workflow reconciliation background service")]
        public static partial void BackgroundServiceStarted(ILogger logger);
    }
}

/// <summary>
/// Background worker that continuously reconciles active deploy workflow operations.
/// </summary>
internal sealed class DeployWorkflowReconcilerBackgroundService(
    IWorkflowOperationStore workflowStore,
    IOperationReconciler reconciler,
    ILogger<DeployWorkflowReconcilerBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DeployWorkflowReconciler.BackgroundLog.BackgroundServiceStarted(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var activeOperations = await workflowStore.ListActiveAsync(WorkflowOperationKind.Deploy, stoppingToken).ConfigureAwait(false);
                foreach (var operation in activeOperations)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        DeployWorkflowReconciler.Log.WorkflowOperationReconcileFailed(logger, operation.OperationId, ex);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                DeployWorkflowReconciler.Log.WorkflowOperationPollLoopFailed(logger, ex);
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class DeployWorkflowReconciler
{
    private async Task ReconcileRampAsync(
        WorkflowOperationRecord operation, IDeployBackend backend, CancellationToken cancellationToken)
    {
        DeploySubmissionResult step;
        try
        {
            step = await backend.StartAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (DeployAdmissionBlockedException ex)
        {
            await MonitorAdmissionHeldRampAsync(operation, backend, ex, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (step.Status == WorkflowOperationStatus.Failed)
        {
            var rollback = await backend.RollbackAsync(operation, cancellationToken).ConfigureAwait(false);
            var status = rollback.Status is WorkflowOperationStatus.RollbackRequested or WorkflowOperationStatus.RolledBack
                ? rollback.Status : WorkflowOperationStatus.ManualInterventionRequired;
            var recovering = operation with
            {
                Status = status,
                CurrentPhase = rollback.Message ?? "Canary ramp failed; rollback requested.",
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = status == WorkflowOperationStatus.ManualInterventionRequired ? DateTimeOffset.UtcNow : null
            };
            await workflowStore.TrySetAsync(recovering, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        var ramp = operation.Deploy?.CanaryRamp ?? throw new InvalidOperationException("Canary ramp is missing.");
        var advanced = operation with
        {
            Status = WorkflowOperationStatus.Reconciling,
            CurrentPhase = step.Message ?? "Canary step submitted.",
            ErrorMessage = null,
            UpdatedAt = DateTimeOffset.UtcNow,
            Deploy = operation.Deploy! with { CanaryRamp = ramp with { CurrentStepStartedAt = DateTimeOffset.UtcNow } }
        };
        await workflowStore.TrySetAsync(advanced, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task MonitorAdmissionHeldRampAsync(
        WorkflowOperationRecord operation,
        IDeployBackend backend,
        DeployAdmissionBlockedException refusal,
        CancellationToken cancellationToken)
    {
        var observation = await backend.ObserveAsync(operation, cancellationToken).ConfigureAwait(false);
        var observed = operation with
        {
            ObservedState = observation.ObservedRevision ?? operation.ObservedState,
            ProviderOperationId = observation.ProviderOperationId ?? operation.ProviderOperationId
        };
        // The next step has not been applied, so its bake clock must stay stopped.
        // Evaluate the currently exposed candidate without allowing forward promotion.
        var monitored = await ApplyRollbackSignalsAsync(
            operation, observed, backend, false, false,
            observation.RollbackRecommended || observation.Status == WorkflowOperationStatus.Failed,
            observation.Message, telemetrySignalEvaluator, cancellationToken).ConfigureAwait(false);
        if (monitored.Status == operation.Status && monitored.Deploy?.Protection?.Phase != DeployProtectionPhase.Recovering)
        {
            monitored = monitored with
            {
                CurrentPhase = operation.CurrentPhase,
                ErrorMessage = refusal.Message,
                CompletedAt = null
            };
        }

        await workflowStore.TrySetAsync(monitored with { UpdatedAt = DateTimeOffset.UtcNow },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WorkflowOperationRecord> ApplyPostActivationProtectionWindowAsync(
        WorkflowOperationRecord current,
        IDeployBackend backend,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ApplyPostActivationProtectionWindowCoreAsync(current, backend, cancellationToken).ConfigureAwait(false);
        }
        catch (DeployAdmissionBlockedException ex)
        {
            return current with { ErrorMessage = ex.Message, UpdatedAt = DateTimeOffset.UtcNow };
        }
    }

    internal static async Task<WorkflowOperationRecord> AdvanceOrPromoteAsync(
        WorkflowOperationRecord current,
        IDeployBackend backend,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AdvanceOrPromoteCoreAsync(current, backend, cancellationToken).ConfigureAwait(false);
        }
        catch (DeployAdmissionBlockedException ex)
        {
            // Keep this pass's observation and telemetry state; the next pass retries the step.
            return current with { ErrorMessage = ex.Message, UpdatedAt = DateTimeOffset.UtcNow };
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(9027, LogLevel.Warning, "Deploy admission refused for operation {OperationId}: {Reason}")]
        public static partial void DeployAdmissionRefused(ILogger logger, string operationId, string reason);
    }
}

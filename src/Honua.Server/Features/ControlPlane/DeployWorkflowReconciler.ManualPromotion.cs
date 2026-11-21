// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class DeployWorkflowReconciler
{
    private async Task ObserveManualPromotionAsync(
        WorkflowOperationRecord operation, IDeployBackend backend, CancellationToken cancellationToken)
    {
        var observation = await backend.ObserveAsync(operation, cancellationToken).ConfigureAwait(false);
        // A provider can have accepted the command before the caller was interrupted. Observation
        // may prove completion; an ambiguous outcome requires operator review rather than replay.
        var status = IsTerminal(observation.Status)
            ? observation.Status : WorkflowOperationStatus.ManualInterventionRequired;
        var observed = operation with
        {
            Status = status,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            ProviderOperationId = observation.ProviderOperationId ?? operation.ProviderOperationId,
            ObservedState = observation.ObservedRevision ?? operation.ObservedState,
            CurrentPhase = observation.Message ?? "Interrupted manual promotion observed; review provider state before continuing.",
            ErrorMessage = status == WorkflowOperationStatus.ManualInterventionRequired
                ? "Manual promotion completion could not be confirmed." : observation.Message,
            Deploy = operation.Deploy! with { ManualPromotionPending = false }
        };
        observed = BeginPostActivationObservationIfPromoted(observed);
        await workflowStore.TrySetAsync(observed, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed class KubernetesGitOpsDeployBackend(ILogger<KubernetesGitOpsDeployBackend> logger)
    : GitOpsDeployBackendBase(logger)
{
    public override string BackendName => "honua-gitops-kubernetes";

    public override DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;
}

internal sealed class AwsEcsGitOpsDeployBackend(ILogger<AwsEcsGitOpsDeployBackend> logger)
    : GitOpsDeployBackendBase(logger)
{
    public override string BackendName => "honua-gitops-aws-ecs";

    public override DeployTargetKind TargetKind => DeployTargetKind.AwsEcs;
}

internal abstract partial class GitOpsDeployBackendBase(ILogger logger) : IDeployBackend
{
    public abstract string BackendName { get; }

    public abstract DeployTargetKind TargetKind { get; }

    public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DeployBackendCapabilities
        {
            SupportsRollback = false,
            SupportsCancellation = false,
            SupportsTrafficShifting = TargetKind is DeployTargetKind.AwsEcs or DeployTargetKind.AwsLambda,
            RequiresOutOfBandMigrations = TargetKind is DeployTargetKind.AwsLambda,
            SupportsProgressPolling = false,
            SupportsRevisionPinning = true
        });

    public async Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();

        var blockingReasons = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(spec.TargetId))
        {
            blockingReasons.Add("A target ID is required.");
        }

        if (string.IsNullOrWhiteSpace(spec.DesiredRevision))
        {
            blockingReasons.Add("A desired revision is required.");
        }

        if (string.IsNullOrWhiteSpace(spec.ArtifactReference))
        {
            warnings.Add("No artifact reference is configured for this target.");
        }

        var capabilities = await GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (spec.RequiresOutOfBandMigrations && !capabilities.RequiresOutOfBandMigrations)
        {
            warnings.Add("This target requires out-of-band migrations even though the backend does not require them globally.");
        }

        return new DeployPlan
        {
            IsReadyToSubmit = blockingReasons.Count == 0 && !spec.RequiresApproval,
            RequiresApproval = spec.RequiresApproval,
            RequiresOutOfBandMigrations = spec.RequiresOutOfBandMigrations,
            BlockingReasons = blockingReasons,
            Warnings = warnings
        };
    }

    public Task<DeploySubmissionResult> StartAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        Log.OperationSubmitted(logger, operation.OperationId, operation.Deploy?.TargetId ?? operation.OperationId);
        return Task.FromResult(new DeploySubmissionResult
        {
            Status = WorkflowOperationStatus.Submitted,
            ProviderOperationId = $"{BackendName}:{operation.OperationId}",
            Message = "Queued for Honua GitOps reconciliation."
        });
    }

    public Task<DeployObservation> ObserveAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new DeployObservation
        {
            Status = WorkflowOperationStatus.ManualInterventionRequired,
            ProviderOperationId = operation.ProviderOperationId,
            ObservedRevision = operation.Status == WorkflowOperationStatus.RollbackRequested
                ? operation.Deploy?.CurrentRevision
                : operation.Deploy?.DesiredRevision,
            Message = operation.Status == WorkflowOperationStatus.RollbackRequested
                ? "Rollback was handed off to the external GitOps controller. Confirm rollback completion out of band."
                : "Deployment was handed off to the external GitOps controller. Confirm rollout completion out of band."
        });
    }

    public Task<DeployObservation> RollbackAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        // Fail loudly, do not silently park (honua-server#2811). This base backend only hands the desired
        // revision to an EXTERNAL GitOps controller; it cannot itself revert the running workload. Earlier
        // it returned RollbackRequested, which the reconciler treats as "rollback took" — reporting a
        // rollback that never happened and only surfacing the truth later as ManualInterventionRequired via
        // ObserveAsync. Returning ManualInterventionRequired here makes the reconciler escalate immediately
        // with a loud error so an operator is paged to perform the out-of-band GitOps revert. Backends that
        // CAN perform a real in-process revert (Argo, YARP) override this.
        Log.RollbackRequiresManualIntervention(logger, operation.OperationId, operation.Deploy?.TargetId ?? operation.OperationId);
        return Task.FromResult(new DeployObservation
        {
            Status = WorkflowOperationStatus.ManualInterventionRequired,
            ProviderOperationId = operation.ProviderOperationId ?? $"{BackendName}:{operation.OperationId}",
            ObservedRevision = operation.Deploy?.CurrentRevision,
            Message = "Automatic rollback is not supported by the Honua GitOps hand-off backend: the desired " +
                "revision is applied by an external GitOps controller, so the rollback must be performed " +
                "out of band (revert the pinned revision in the GitOps repository). This operation requires " +
                "manual intervention rather than being reported as rolled back."
        });
    }

    private static partial class Log
    {
        [LoggerMessage(9020, LogLevel.Information, "Submitted deploy workflow operation {OperationId} for target {TargetId}")]
        public static partial void OperationSubmitted(ILogger logger, string operationId, string targetId);

        [LoggerMessage(9021, LogLevel.Warning, "Automatic rollback for workflow operation {OperationId} targeting {TargetId} requires manual out-of-band GitOps intervention")]
        public static partial void RollbackRequiresManualIntervention(ILogger logger, string operationId, string targetId);
    }
}

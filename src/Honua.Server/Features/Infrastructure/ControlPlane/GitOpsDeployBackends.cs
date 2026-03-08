// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Built-in GitOps deploy backend for Kubernetes targets managed by Honua.
/// </summary>
internal sealed class KubernetesGitOpsDeployBackend(ILogger<KubernetesGitOpsDeployBackend> logger)
    : GitOpsDeployBackendBase(logger)
{
    public override string BackendName => "honua-gitops-kubernetes";

    public override DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;
}

/// <summary>
/// Built-in GitOps deploy backend for AWS ECS targets managed by Honua.
/// </summary>
internal sealed class AwsEcsGitOpsDeployBackend(ILogger<AwsEcsGitOpsDeployBackend> logger)
    : GitOpsDeployBackendBase(logger)
{
    public override string BackendName => "honua-gitops-aws-ecs";

    public override DeployTargetKind TargetKind => DeployTargetKind.AwsEcs;
}

/// <summary>
/// Built-in GitOps deploy backend for AWS Lambda targets managed by Honua.
/// </summary>
internal sealed class AwsLambdaGitOpsDeployBackend(ILogger<AwsLambdaGitOpsDeployBackend> logger)
    : GitOpsDeployBackendBase(logger)
{
    public override string BackendName => "honua-gitops-aws-lambda";

    public override DeployTargetKind TargetKind => DeployTargetKind.AwsLambda;
}

/// <summary>
/// Built-in GitOps deploy backend for Azure Functions targets managed by Honua.
/// </summary>
internal sealed class AzureFunctionsGitOpsDeployBackend(ILogger<AzureFunctionsGitOpsDeployBackend> logger)
    : GitOpsDeployBackendBase(logger)
{
    public override string BackendName => "honua-gitops-azure-functions";

    public override DeployTargetKind TargetKind => DeployTargetKind.AzureFunctions;
}

internal abstract partial class GitOpsDeployBackendBase(ILogger logger) : IDeployBackend
{
    public abstract string BackendName { get; }

    public abstract DeployTargetKind TargetKind { get; }

    public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DeployBackendCapabilities
        {
            SupportsRollback = true,
            SupportsCancellation = false,
            SupportsTrafficShifting = TargetKind is DeployTargetKind.AwsEcs or DeployTargetKind.AwsLambda or DeployTargetKind.AzureFunctions,
            RequiresOutOfBandMigrations = TargetKind is DeployTargetKind.AwsLambda or DeployTargetKind.AzureFunctions,
            SupportsProgressPolling = true,
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

        var (status, observedRevision, message) = operation.Status switch
        {
            WorkflowOperationStatus.Submitted => (
                WorkflowOperationStatus.Reconciling,
                operation.Deploy?.CurrentRevision,
                "Deploy is reconciling through Honua GitOps controller."),
            WorkflowOperationStatus.Reconciling => (
                WorkflowOperationStatus.Succeeded,
                operation.Deploy?.DesiredRevision,
                "Deploy reconciled through Honua GitOps controller."),
            WorkflowOperationStatus.RollbackRequested => (
                WorkflowOperationStatus.RolledBack,
                operation.Deploy?.CurrentRevision,
                "Rollback completed through Honua GitOps reconciliation."),
            _ => (
                operation.Status,
                operation.Status == WorkflowOperationStatus.Succeeded
                    ? operation.Deploy?.DesiredRevision
                    : operation.Deploy?.CurrentRevision,
                operation.CurrentPhase)
        };

        return Task.FromResult(new DeployObservation
        {
            Status = status,
            ProviderOperationId = operation.ProviderOperationId,
            ObservedRevision = observedRevision,
            Message = message
        });
    }

    public Task<DeployObservation> RollbackAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        Log.RollbackRequested(logger, operation.OperationId, operation.Deploy?.TargetId ?? operation.OperationId);
        return Task.FromResult(new DeployObservation
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            ProviderOperationId = operation.ProviderOperationId ?? $"{BackendName}:{operation.OperationId}",
            ObservedRevision = operation.Deploy?.CurrentRevision,
            Message = "Rollback requested through Honua GitOps reconciliation."
        });
    }

    private static partial class Log
    {
        [LoggerMessage(9020, LogLevel.Information, "Submitted deploy workflow operation {OperationId} for target {TargetId}")]
        public static partial void OperationSubmitted(ILogger logger, string operationId, string targetId);

        [LoggerMessage(9021, LogLevel.Warning, "Rollback requested for workflow operation {OperationId} targeting {TargetId}")]
        public static partial void RollbackRequested(ILogger logger, string operationId, string targetId);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

// The event only selects which operation to reconcile, and the handler never treats the payload as authoritative state.
internal sealed partial class ControlPlaneEventHandler(
    IExecutionJobStore jobStore,
    IWorkflowOperationStore workflowStore,
    IOperationReconcileDispatcher dispatcher,
    ILogger<ControlPlaneEventHandler> logger)
{
    public async Task HandleExecutionJobEventAsync(
        string providerOperationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerOperationId))
        {
            return;
        }

        var operationId = await ResolveExecutionOperationIdAsync(providerOperationId, cancellationToken).ConfigureAwait(false);
        if (operationId is null)
        {
            Log.UnresolvedExecutionEvent(logger, providerOperationId);
            return;
        }

        Log.HandlingExecutionEvent(logger, providerOperationId, operationId);
        await dispatcher
            .ReconcileOnceAsync(new OperationRef(OperationKind.ExecutionJob, operationId), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task HandleExecutionJobOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return Task.CompletedTask;
        }

        Log.HandlingExecutionEvent(logger, "(operation-id)", operationId);
        return dispatcher.ReconcileOnceAsync(
            new OperationRef(OperationKind.ExecutionJob, operationId),
            cancellationToken);
    }

    public async Task HandleDeployEventAsync(
        string providerOperationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerOperationId))
        {
            return;
        }

        var operationId = await ResolveWorkflowOperationIdAsync(
                WorkflowOperationKind.Deploy,
                providerOperationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (operationId is null)
        {
            Log.UnresolvedDeployEvent(logger, providerOperationId);
            return;
        }

        Log.HandlingDeployEvent(logger, providerOperationId, operationId);
        await dispatcher
            .ReconcileOnceAsync(new OperationRef(OperationKind.DeployWorkflow, operationId), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task HandleDeployOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return Task.CompletedTask;
        }

        Log.HandlingDeployEvent(logger, "(operation-id)", operationId);
        return dispatcher.ReconcileOnceAsync(
            new OperationRef(OperationKind.DeployWorkflow, operationId),
            cancellationToken);
    }

    // A staged release advances one stage per call, so a persisted stage enqueues a continue signal, and a terminal status makes a further reconcile a no-op.
    public Task HandleStagedContinueAsync(
        OperationKind kind,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return Task.CompletedTask;
        }

        if (kind is not (OperationKind.MetadataRelease or OperationKind.CoordinatedRelease or OperationKind.PreMigrationRestore))
        {
            Log.UnsupportedStagedContinue(logger, kind.ToString(), operationId);
            return Task.CompletedTask;
        }

        Log.HandlingStagedContinue(logger, kind.ToString(), operationId);
        return dispatcher.ReconcileOnceAsync(new OperationRef(kind, operationId), cancellationToken);
    }

    private async Task<string?> ResolveExecutionOperationIdAsync(
        string providerOperationId,
        CancellationToken cancellationToken)
    {
        var active = await jobStore.ListActiveAsync(kind: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        return active
            .FirstOrDefault(job => string.Equals(job.ProviderOperationId, providerOperationId, StringComparison.Ordinal))
            ?.OperationId;
    }

    private async Task<string?> ResolveWorkflowOperationIdAsync(
        WorkflowOperationKind kind,
        string providerOperationId,
        CancellationToken cancellationToken)
    {
        var active = await workflowStore.ListActiveAsync(kind, cancellationToken).ConfigureAwait(false);
        return active
            .FirstOrDefault(operation => string.Equals(operation.ProviderOperationId, providerOperationId, StringComparison.Ordinal))
            ?.OperationId;
    }

    private static partial class Log
    {
        [LoggerMessage(9040, LogLevel.Debug, "Handling execution-job state-change event for provider {ProviderOperationId} -> operation {OperationId}")]
        public static partial void HandlingExecutionEvent(ILogger logger, string providerOperationId, string operationId);

        [LoggerMessage(9041, LogLevel.Debug, "Execution-job state-change event for provider {ProviderOperationId} matched no active operation; ignoring")]
        public static partial void UnresolvedExecutionEvent(ILogger logger, string providerOperationId);

        [LoggerMessage(9046, LogLevel.Debug, "Handling deploy state-change event for provider {ProviderOperationId} -> operation {OperationId}")]
        public static partial void HandlingDeployEvent(ILogger logger, string providerOperationId, string operationId);

        [LoggerMessage(9047, LogLevel.Debug, "Deploy state-change event for provider {ProviderOperationId} matched no active deploy operation; ignoring")]
        public static partial void UnresolvedDeployEvent(ILogger logger, string providerOperationId);

        [LoggerMessage(9048, LogLevel.Debug, "Handling staged-release continue signal for {Kind} operation {OperationId}")]
        public static partial void HandlingStagedContinue(ILogger logger, string kind, string operationId);

        [LoggerMessage(9049, LogLevel.Warning, "Staged-release continue signal for unsupported kind {Kind} (operation {OperationId}); ignoring")]
        public static partial void UnsupportedStagedContinue(ILogger logger, string kind, string operationId);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Event entrypoint that drives a single control-plane reconcile from an external state-change
/// event instead of a poll cycle. This is what an AWS Lambda â€” triggered by an EventBridge rule â€”
/// invokes: resolve the event to the durable operation id, then reconcile that one operation
/// exactly once and exit.
/// <para>
/// Phase 1 covers execution jobs (AWS Batch). Phase 2 extends the same loop-free, payload-untrusted
/// pattern to the deploy/release reconcilers:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Deploy workflows</b> (<see cref="HandleDeployEventAsync"/>): driven by ECS Task State
///     Change, CodeDeploy DeploymentStateChange, and Lambda-alias events for the
///     <c>AwsEcsAlbDeployBackend</c> / <c>AwsLambdaGitOpsDeployBackend</c> targets. The event only
///     selects WHICH deploy operation to reconcile; the reconciler re-reads authoritative provider
///     state through the backend's <c>ObserveAsync</c> and advances under its own lease + CAS.
///   </description></item>
///   <item><description>
///     <b>Staged releases</b> (metadata-release / coordinated-release) advance one stage per
///     reconcile. After a stage persists, the reconciler can enqueue a self-continue signal that
///     <see cref="HandleStagedContinueAsync"/> consumes to drive the next stage immediately, so a
///     multi-stage release does not have to wait for the coarse backstop between stages. The
///     data-populate stage completion arrives as a Batch event and reuses the Phase-1
///     <see cref="HandleExecutionJobEventAsync"/> path.
///   </description></item>
/// </list>
/// <para>
/// The handler is intentionally loop-free and never trusts the event payload as authoritative
/// state. A malformed, duplicate, or out-of-order event therefore cannot corrupt state â€” at worst it
/// triggers a redundant reconcile that the lease/CAS + terminal guards inside each reconciler make a
/// no-op.
/// </para>
/// </summary>
internal sealed partial class ControlPlaneEventHandler(
    IExecutionJobStore jobStore,
    IWorkflowOperationStore workflowStore,
    IOperationReconcileDispatcher dispatcher,
    ILogger<ControlPlaneEventHandler> logger)
{
    /// <summary>
    /// Handles a batch execution-job state-change event by reconciling the matching operation once.
    /// </summary>
    /// <param name="providerOperationId">
    /// The provider/backend job id carried by the event (for AWS Batch this is
    /// <c>detail.jobId</c>). Resolved to the durable operation id via the execution-job store.
    /// If no active job matches, the event is treated as already-handled (terminal or unknown)
    /// and ignored.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>
    /// Handles an event that already carries the durable operation id (for callers that mapped the
    /// provider id upstream). Reconciles the operation once.
    /// </summary>
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

    /// <summary>
    /// Handles a deploy provider state-change event by reconciling the matching deploy workflow once.
    /// <para>
    /// The cloud wiring (built by the parallel IaC agent, NOT in this PR) maps the following
    /// EventBridge events to this handler, passing the provider id the event carries:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>ECS Task State Change</b> and <b>CodeDeploy DeploymentStateChange</b> for the
    ///     <c>AwsEcsAlbDeployBackend</c> â€” the provider id is the CodeDeploy deployment id /
    ///     ECS service-deployment id recorded as the operation's <c>ProviderOperationId</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Lambda-alias</b> routing-config change events for the
    ///     <c>AwsLambdaGitOpsDeployBackend</c> â€” the provider id is the alias/version recorded as
    ///     the operation's <c>ProviderOperationId</c>.
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <param name="providerOperationId">
    /// The deploy provider id carried by the event (CodeDeploy deployment id, ECS service-deployment
    /// id, or Lambda alias/version). Resolved to the durable operation id via the workflow store.
    /// If no active deploy operation matches, the event is ignored (terminal or unknown).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>
    /// Handles a deploy event that already carries the durable operation id. Reconciles once.
    /// </summary>
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

    /// <summary>
    /// Self-continue entrypoint for a staged release (metadata-release or coordinated-release).
    /// <para>
    /// These reconcilers advance exactly one stage per call. Under event mode there is no 5s poll
    /// loop to re-enter and drive the next stage, so after a stage persists the reconciler enqueues a
    /// "continue" signal (a custom EventBridge event) that this handler consumes to reconcile the
    /// same operation again â€” advancing the next stage. The chain terminates naturally when the
    /// operation reaches a terminal status, because the reconciler's terminal guard makes any further
    /// reconcile a no-op. The backstop is the safety net if a continue signal is dropped.
    /// </para>
    /// </summary>
    /// <param name="kind">The staged operation family (MetadataRelease or CoordinatedRelease).</param>
    /// <param name="operationId">The durable staged-operation id to advance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task HandleStagedContinueAsync(
        OperationKind kind,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return Task.CompletedTask;
        }

        if (kind is not (OperationKind.MetadataRelease or OperationKind.CoordinatedRelease))
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

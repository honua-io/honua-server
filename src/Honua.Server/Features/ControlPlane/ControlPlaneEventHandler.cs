// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.ControlPlane;

/// <summary>
/// Event entrypoint that drives a single execution-job reconcile from an external state-change
/// event instead of a poll cycle. This is what an AWS Lambda — triggered by an EventBridge
/// "Batch Job State Change" event — invokes: resolve the provider job id to the durable
/// operation id, then reconcile that one operation exactly once and exit.
/// <para>
/// The handler is intentionally loop-free and side-effect-light: it never trusts the event
/// payload as authoritative state. It only selects WHICH operation to reconcile; the reconciler
/// then re-reads authoritative provider state via the backend's <c>ObserveAsync</c>/<c>DescribeJob</c>
/// and advances under its own lease + CAS. A malformed, duplicate, or out-of-order event therefore
/// cannot corrupt state — at worst it triggers a redundant reconcile that the lease/CAS make a no-op.
/// </para>
/// </summary>
internal sealed partial class ControlPlaneEventHandler(
    IExecutionJobStore jobStore,
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

        var operationId = await ResolveOperationIdAsync(providerOperationId, cancellationToken).ConfigureAwait(false);
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

    private async Task<string?> ResolveOperationIdAsync(
        string providerOperationId,
        CancellationToken cancellationToken)
    {
        var active = await jobStore.ListActiveAsync(kind: null, cancellationToken).ConfigureAwait(false);
        foreach (var job in active)
        {
            if (string.Equals(job.ProviderOperationId, providerOperationId, StringComparison.Ordinal))
            {
                return job.OperationId;
            }
        }

        return null;
    }

    private static partial class Log
    {
        [LoggerMessage(9040, LogLevel.Debug, "Handling execution-job state-change event for provider {ProviderOperationId} -> operation {OperationId}")]
        public static partial void HandlingExecutionEvent(ILogger logger, string providerOperationId, string operationId);

        [LoggerMessage(9041, LogLevel.Debug, "Execution-job state-change event for provider {ProviderOperationId} matched no active operation; ignoring")]
        public static partial void UnresolvedExecutionEvent(ILogger logger, string providerOperationId);
    }
}

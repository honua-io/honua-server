// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Decorates the durable workflow-operation store to raise <see cref="IWorkflowOperationTransitionListener"/>
/// observers on operator-meaningful transitions (created / submitted / promoted / rolled-back /
/// manual-intervention). Every persisted transition from both <c>DeployWorkflowService</c> and
/// <c>DeployWorkflowReconciler</c> funnels through <see cref="IWorkflowOperationStore.SetAsync"/> /
/// <see cref="IWorkflowOperationStore.TrySetAsync"/> / <see cref="IWorkflowOperationStore.TryCreateAsync"/>,
/// so decorating those three write points is a single seam that covers them all without scattering
/// notification calls across the reconciler. Listeners are invoked best-effort AFTER the authoritative
/// inner write; a throwing listener is isolated and never fails the write or starves siblings. Reads,
/// leases, and queries pass straight through.
/// </summary>
internal sealed partial class TransitionObservingWorkflowOperationStore : IWorkflowOperationStore
{
    private readonly IWorkflowOperationStore _inner;
    private readonly IWorkflowOperationTransitionListener[] _listeners;
    private readonly ILogger<TransitionObservingWorkflowOperationStore> _logger;

    public TransitionObservingWorkflowOperationStore(
        IWorkflowOperationStore inner,
        IEnumerable<IWorkflowOperationTransitionListener> listeners,
        ILogger<TransitionObservingWorkflowOperationStore> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _listeners = (listeners ?? throw new ArgumentNullException(nameof(listeners))).ToArray();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var created = await _inner.TryCreateAsync(operation, ttl, cancellationToken).ConfigureAwait(false);
        if (created)
        {
            await RaiseAsync(operation, WorkflowOperationTransitionKind.Created, cancellationToken).ConfigureAwait(false);
        }

        return created;
    }

    public async Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _inner.SetAsync(operation, ttl, cancellationToken).ConfigureAwait(false);
        await RaiseClassifiedAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var updated = await _inner.TrySetAsync(operation, ttl, cancellationToken).ConfigureAwait(false);
        if (updated)
        {
            await RaiseClassifiedAsync(operation, cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    private Task RaiseClassifiedAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken)
        => TryClassify(operation.Status, out var kind)
            ? RaiseAsync(operation, kind, cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Maps a persisted status to a transition kind. Intermediate states (planned / awaiting approval /
    /// reconciling / failed) are intentionally not surfaced: the seam reports operator-meaningful
    /// lifecycle moments, and mapping only <see cref="WorkflowOperationStatus.Submitted"/> (not the
    /// repeatedly-written <see cref="WorkflowOperationStatus.Reconciling"/>) keeps in-flight polling from
    /// emitting duplicate submitted events.
    /// </summary>
    private static bool TryClassify(WorkflowOperationStatus status, out WorkflowOperationTransitionKind kind)
    {
        switch (status)
        {
            case WorkflowOperationStatus.Submitted:
                kind = WorkflowOperationTransitionKind.Submitted;
                return true;
            case WorkflowOperationStatus.Succeeded:
                kind = WorkflowOperationTransitionKind.Promoted;
                return true;
            case WorkflowOperationStatus.RollbackRequested:
            case WorkflowOperationStatus.RolledBack:
                kind = WorkflowOperationTransitionKind.RolledBack;
                return true;
            case WorkflowOperationStatus.ManualInterventionRequired:
                kind = WorkflowOperationTransitionKind.ManualInterventionRequired;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private async Task RaiseAsync(WorkflowOperationRecord operation, WorkflowOperationTransitionKind kind, CancellationToken cancellationToken)
    {
        if (_listeners.Length == 0)
        {
            return;
        }

        var transition = new WorkflowOperationTransition
        {
            Operation = operation,
            Kind = kind,
            OccurredAt = DateTimeOffset.UtcNow
        };

        foreach (var listener in _listeners)
        {
            try
            {
                await listener.OnTransitionAsync(transition, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            // Intentional catch-all: this is a per-listener loop broadcasting a
            // workflow-operation transition; one listener's failure must not
            // stop the transition from reaching the remaining listeners.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogListenerFailed(_logger, operation.OperationId, kind.ToString(), ex);
            }
        }
    }

    // Straight pass-through for the remaining store surface.
    public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
        => _inner.GetAsync(operationId, cancellationToken);

    public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
        => _inner.GetByMetadataPackageIdAsync(packageId, cancellationToken);

    public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        => _inner.ListActiveAsync(kind, cancellationToken);

    public Task<WorkflowOperationPage> QueryAsync(WorkflowOperationQuery query, CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);

    public Task<WorkflowOperationRecord?> GetMostRecentSucceededDeployByTargetAsync(string targetId, CancellationToken cancellationToken = default)
        => _inner.GetMostRecentSucceededDeployByTargetAsync(targetId, cancellationToken);

    public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => _inner.TryAcquireLeaseAsync(operationId, ownerId, leaseDuration, cancellationToken);

    public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => _inner.RenewLeaseAsync(operationId, ownerId, leaseDuration, cancellationToken);

    public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        => _inner.ReleaseLeaseAsync(operationId, ownerId, cancellationToken);

    [LoggerMessage(EventId = 9460, Level = LogLevel.Warning, Message = "Workflow transition listener failed for operation {OperationId} ({TransitionKind}).")]
    private static partial void LogListenerFailed(ILogger logger, string operationId, string transitionKind, Exception exception);
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>Serializes deploy admission with durable restore ownership.</summary>
internal sealed class RestoreDeployAdmission : IAsyncDisposable
{
    internal const string CellOwnerId = "restore-cell-exclusive-owner";
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private readonly IWorkflowOperationStore _store;
    private readonly string _leaseOwner;
    private readonly string _leaseKey;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _renewal;

    private RestoreDeployAdmission(IWorkflowOperationStore store, string leaseKey, string leaseOwner, CancellationToken cancellationToken)
    {
        _store = store;
        _leaseOwner = leaseOwner;
        _leaseKey = leaseKey;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _renewal = RenewAsync();
    }

    internal CancellationToken Token => _cancellation.Token;

    internal static async Task<RestoreDeployAdmission> AcquireAsync(IWorkflowOperationStore store, string workflowId, string? restoreOwnerId, bool allowRestorePromotion, CancellationToken cancellationToken)
    {
        var leaseOwner = $"deploy-admission:{Guid.NewGuid():N}";
        if (!await store.TryAcquireLeaseAsync(CellOwnerId, leaseOwner, Lease, cancellationToken).ConfigureAwait(false))
            throw new ResourceConflictException("Deploy admission is busy with a restore or another deploy.");
        var admission = new RestoreDeployAdmission(store, CellOwnerId, leaseOwner, cancellationToken);
        try
        {
            var owner = await store.GetAsync(CellOwnerId, admission.Token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(owner?.CurrentPhase))
            {
                var restore = await store.GetAsync(owner.CurrentPhase, admission.Token).ConfigureAwait(false);
                if (await CanReleaseOwnerAsync(store, restore, admission.Token).ConfigureAwait(false))
                {
                    // Completion can race another lease holder. Clear only a durably terminal owner,
                    // under the same lease, and fail closed if the versioned write loses a race.
                    if (!await store.TrySetAsync(owner with { CurrentPhase = null, UpdatedAt = DateTimeOffset.UtcNow },
                            TimeSpan.FromDays(365), admission.Token).ConfigureAwait(false))
                        throw new ResourceConflictException("Restore owner cleanup must be retried.");
                }
                else
                {
                    var checkpoint = restore?.PreMigrationRestore;
                    if ((!allowRestorePromotion && !string.Equals(owner.CurrentPhase, restoreOwnerId, StringComparison.Ordinal)) ||
                        checkpoint?.Phase is not ("awaiting-deploy" or "awaiting-compatible-deploy" or "awaiting-local-deploy") ||
                        !string.Equals(checkpoint.DeployOperationId, workflowId, StringComparison.Ordinal))
                        throw new ResourceConflictException("Deploy admission is blocked by a durable restore.");
                }

            }
            return admission;
        }
        catch
        {
            await admission.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // A failed wrapper is insufficient when its child may still be changing the substrate.
    // Missing records and interrupted provider submissions remain fenced for investigation.
    internal static async Task<bool> CanReleaseOwnerAsync(
        IWorkflowOperationStore store, WorkflowOperationRecord? operation, CancellationToken cancellationToken)
    {
        if (operation?.Kind != WorkflowOperationKind.PreMigrationRestore) return false;
        if (operation.Status == WorkflowOperationStatus.Succeeded) return true;
        if (operation.Status != WorkflowOperationStatus.Failed || operation.PreMigrationRestore is not { } checkpoint) return false;
        if (string.IsNullOrWhiteSpace(checkpoint.DeployOperationId)) return checkpoint.Phase == "queued";
        var child = await store.GetAsync(checkpoint.DeployOperationId, cancellationToken).ConfigureAwait(false);
        // Remote providers can keep executing after a failed response; their terminal records
        // alone do not establish quiescence. Automatic failure recovery is local Compose only.
        return child?.Kind == WorkflowOperationKind.Deploy && child.Deploy?.TargetKind == DeployTargetKind.LocalCompose &&
            child.Status is WorkflowOperationStatus.Succeeded or WorkflowOperationStatus.Failed or WorkflowOperationStatus.RolledBack;
    }

    internal static async Task<RestoreDeployAdmission> AcquireWorkflowAsync(IWorkflowOperationStore store, string workflowId, CancellationToken cancellationToken)
    {
        var leaseOwner = $"deploy-submission:{Guid.NewGuid():N}";
        if (!await store.TryAcquireLeaseAsync(workflowId, leaseOwner, Lease, cancellationToken).ConfigureAwait(false))
            throw new ResourceConflictException("Deploy submission is being reconciled.");
        return new RestoreDeployAdmission(store, workflowId, leaseOwner, cancellationToken);
    }

    private async Task RenewAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cancellation.Token).ConfigureAwait(false);
                if (!await _store.RenewLeaseAsync(_leaseKey, _leaseOwner, Lease, _cancellation.Token).ConfigureAwait(false))
                {
                    await _cancellation.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        await _renewal.ConfigureAwait(false);
        await _store.ReleaseLeaseAsync(_leaseKey, _leaseOwner, CancellationToken.None).ConfigureAwait(false);
        _cancellation.Dispose();
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.ControlPlane;

/// <summary>Serializes deploy admission with durable restore ownership.</summary>
internal sealed class RestoreDeployAdmission : IAsyncDisposable
{
    internal const string CellOwnerId = "restore-cell-exclusive-owner";
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private readonly IWorkflowOperationStore _store;
    private readonly string _leaseOwner;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _renewal;

    private RestoreDeployAdmission(IWorkflowOperationStore store, string leaseOwner, CancellationToken cancellationToken)
    {
        _store = store;
        _leaseOwner = leaseOwner;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _renewal = RenewAsync();
    }

    internal CancellationToken Token => _cancellation.Token;

    internal static async Task<RestoreDeployAdmission> AcquireAsync(IWorkflowOperationStore store, string workflowId, string? restoreOwnerId, CancellationToken cancellationToken, bool allowRestorePromotion = false)
    {
        var leaseOwner = $"deploy-admission:{Guid.NewGuid():N}";
        if (!await store.TryAcquireLeaseAsync(CellOwnerId, leaseOwner, Lease, cancellationToken).ConfigureAwait(false))
            throw new ResourceConflictException("Deploy admission is busy with a restore or another deploy.");
        var admission = new RestoreDeployAdmission(store, leaseOwner, cancellationToken);
        try
        {
            var owner = await store.GetAsync(CellOwnerId, admission.Token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(owner?.CurrentPhase))
            {
                var restore = await store.GetAsync(owner.CurrentPhase, admission.Token).ConfigureAwait(false);
                var checkpoint = restore?.PreMigrationRestore;
                if ((!allowRestorePromotion && !string.Equals(owner.CurrentPhase, restoreOwnerId, StringComparison.Ordinal)) ||
                    checkpoint?.Phase is not ("awaiting-deploy" or "awaiting-compatible-deploy" or "awaiting-local-deploy") ||
                    !string.Equals(checkpoint.DeployOperationId, workflowId, StringComparison.Ordinal))
                    throw new ResourceConflictException("Deploy admission is blocked by a durable restore.");
            }
            return admission;
        }
        catch
        {
            await admission.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task RenewAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cancellation.Token).ConfigureAwait(false);
                if (!await _store.RenewLeaseAsync(CellOwnerId, _leaseOwner, Lease, _cancellation.Token).ConfigureAwait(false))
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
        await _store.ReleaseLeaseAsync(CellOwnerId, _leaseOwner, CancellationToken.None).ConfigureAwait(false);
        _cancellation.Dispose();
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;

namespace Honua.Infrastructure.SharedCaching;

// Edit purges: the coalescing window, the leases that share it across tasks, and what happens to paths that were not sent.
internal sealed partial class CdnInvalidationService
{
    private async Task InvalidatePendingWhenWindowClosesAsync(DateTimeOffset windowClosesAt)
    {
        var delay = windowClosesAt - _timeProvider.GetUtcNow();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider).ConfigureAwait(false);
        }

        string[] paths;
        lock (_gate)
        {
            if (_windowClosesAt != windowClosesAt)
            {
                return;
            }

            paths = [.. _pendingEditPaths];
            _pendingEditPaths.Clear();
            _windowClosesAt = null;
            _lastEditInvalidation = _timeProvider.GetUtcNow();
        }

        var result = await SendAfterEditAsync(paths, CdnPurgeCause.Edit(CdnPurgeCause.CoalescedEdits), CancellationToken.None)
            .ConfigureAwait(false);
        // A busy CDN has already been handed the paths again for the next window.
        if (result is { Outcome: CdnInvalidationOutcome.Failed })
        {
            Log.EditInvalidationFailed(logger, result.Detail ?? "unknown");
        }
    }

    // An address another task invalidated within the window is retried shortly: that purge may have preceded this edit.
    private async Task<CdnInvalidationResult?> SendAfterEditAsync(string[] paths, CdnPurgeCause cause, CancellationToken cancellationToken)
    {
        string[] claimed;
        CdnInvalidationResult result;
        try
        {
            claimed = await ClaimAcrossTasksAsync(paths, cancellationToken).ConfigureAwait(false);
            // A settle repeat whose address another task holds is dropped: that task's purge is newer and settles itself.
            if (claimed.Length < paths.Length && cause.ChangeId != CdnPurgeCause.SettledEdits)
            {
                DeferToNextWindow(paths.Except(claimed, StringComparer.Ordinal), HeldLeaseRetryDelay());
            }

            if (claimed.Length == 0)
            {
                return null;
            }

            var batches = await SendWithinLimitsAsync(claimed, cause, cancellationToken).ConfigureAwait(false);
            result = batches.Result;

            // This task holds the leases, so no other task will send these; a CDN still busy after the retries gets them next window.
            if (batches.BusyPaths.Count > 0)
            {
                if (cause.ChangeId == CdnPurgeCause.SettledEdits)
                {
                    _delivery.SettleOnly(batches.BusyPaths);
                }

                DeferToNextWindow(batches.BusyPaths, CurrentCoalescingWindow());
            }

            AfterEditPurge(claimed, result, cause);
        }
        catch (OperationCanceledException)
        {
            // The edit request gave up, not the edit: its purge goes out with the next window.
            DeferToNextWindow(paths, CurrentCoalescingWindow());
            throw;
        }

        return result;
    }

    internal void InvalidateWithNextWindow(IEnumerable<string> paths)
    {
        if (CdnConfigured)
        {
            DeferToNextWindow(paths, CurrentCoalescingWindow());
        }
    }

    private void DeferToNextWindow(IEnumerable<string> paths, TimeSpan delay)
    {
        var due = _timeProvider.GetUtcNow() + delay;
        lock (_gate)
        {
            _pendingEditPaths.UnionWith(paths);
            if (_windowClosesAt is { } scheduled && scheduled <= due)
            {
                return;
            }

            _windowClosesAt = due;
            _lastEditInvalidation = _timeProvider.GetUtcNow();
        }

        _ = InvalidatePendingWhenWindowClosesAsync(due);
    }

    // One lease per address in the shared database: whichever task takes it sends the purge, and nobody else does until it expires.
    // A host without that database is one task, and its own window is enough.
    private async Task<string[]> ClaimAcrossTasksAsync(string[] paths, CancellationToken cancellationToken)
    {
        if (_leaseStore is null || paths.Length == 0)
        {
            return paths;
        }

        var lease = CurrentCoalescingWindow() - LeaseMargin;
        var claims = new bool[paths.Length];
        await Parallel.ForAsync(
            0,
            paths.Length,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentLeaseClaims, CancellationToken = cancellationToken },
            async (index, token) => claims[index] = await TryClaimAsync(paths[index], lease, token).ConfigureAwait(false))
            .ConfigureAwait(false);
        return [.. paths.Where((_, index) => claims[index])];
    }

    // An unreachable lease store must not leave content stale: purging twice is cheaper than serving the old version.
    private async Task<bool> TryClaimAsync(string path, TimeSpan lease, CancellationToken cancellationToken)
    {
        try
        {
            // A normal-window lease must not delay an emergency purge; emergency tasks still coalesce together.
            var prefix = lease == EmergencyCoalescingWindow - LeaseMargin ? LeaseKeyPrefix + "emergency:" : LeaseKeyPrefix;
            return await _leaseStore!.TryAcquireAsync(new LeaseClaim(prefix + path, _leaseOwner), lease, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.PurgeLeaseUnavailable(logger, exception, path);
            return true;
        }
    }

    // The store does not say when another task's lease ends, so a held address is retried several times per window.
    private TimeSpan HeldLeaseRetryDelay() => CurrentCoalescingWindow() / 6;
}

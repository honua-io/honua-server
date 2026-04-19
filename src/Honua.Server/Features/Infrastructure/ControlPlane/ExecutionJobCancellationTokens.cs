// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Tracks per-job cancellation token sources for execution jobs so that in-flight
/// work can be aborted when a job is cancelled via the API or admin endpoint.
/// Follows the same pattern as <c>PrintJobCancellationTokens</c>.
/// </summary>
/// <remarks>
/// Each tracked entry stores a claim handle (typically the workerId) alongside the CTS.
/// <see cref="Remove"/> and <see cref="Revoke"/> only act when the caller's claim handle
/// matches the tracked entry, preventing a stale worker's cleanup from removing a CTS
/// registered by a new worker that reclaimed the same job after a requeue.
/// </remarks>
internal sealed class ExecutionJobCancellationTokens : IJobCancellationNotifier
{
    private sealed record CancellationEntry(CancellationTokenSource Cts, string ClaimHandle);

    private readonly ConcurrentDictionary<string, CancellationEntry> _tokens = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a linked <see cref="CancellationTokenSource"/> for the job and tracks it
    /// alongside <paramref name="claimHandle"/>.
    /// The returned source is linked to the supplied tokens so host shutdown and timeout
    /// also cancel job work.
    /// </summary>
    public CancellationTokenSource CreateLinkedTokenSource(string jobId, string claimHandle, params CancellationToken[] linkedTokens)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedTokens);
        var newEntry = new CancellationEntry(cts, claimHandle);
        _tokens.AddOrUpdate(jobId, newEntry, (_, existing) =>
        {
            try { existing.Cts.Cancel(); }
            catch (ObjectDisposedException)
            {
                // Expected race: existing CTS was disposed by the prior job-completion
                // path between AddOrUpdate lookup and Cancel. Benign and documented.
            }
            return newEntry;
        });
        return cts;
    }

    /// <inheritdoc />
    public bool Cancel(string jobId)
    {
        if (_tokens.TryGetValue(jobId, out var entry))
        {
            try
            {
                entry.Cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                // The execution service disposed the CTS between our TryGetValue
                // and this Cancel call — the job already completed, so this is a no-op.
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Cancels and removes the tracked token source for the specified job, but only
    /// when the tracked entry's claim handle matches <paramref name="claimHandle"/>.
    /// Used by the reconciliation service to clean up stale tokens when a job
    /// is requeued or terminally failed, ensuring that subsequent
    /// <see cref="Cancel"/> calls do not return a false positive for a
    /// token that no longer corresponds to an active execution.
    /// </summary>
    /// <remarks>
    /// If a new worker has already registered a CTS for this job (different claim handle),
    /// this method is a no-op — the new worker's CTS is preserved.
    /// </remarks>
    public void Revoke(string jobId, string claimHandle)
    {
        if (_tokens.TryGetValue(jobId, out var entry) && entry.ClaimHandle == claimHandle)
        {
            // Atomic compare-and-remove: only succeeds when the entry (CTS reference +
            // handle) still matches, preventing removal of a CTS registered by a new claim.
            // CancellationEntry is a record — equality includes CTS reference equality,
            // so a replacement entry with a different CTS instance will not match.
            if (TryRemoveEntry(jobId, entry))
            {
                try
                {
                    entry.Cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The execution service disposed the CTS — the job already completed.
                }
            }
        }
    }

    /// <summary>
    /// Removes the tracked token source after job completion or cancellation, but only
    /// when the tracked entry's claim handle matches <paramref name="claimHandle"/>.
    /// </summary>
    /// <remarks>
    /// If a new worker reclaimed the job and registered a fresh CTS between the requeue
    /// and this cleanup, the new entry is preserved.
    /// </remarks>
    public void Remove(string jobId, string claimHandle)
    {
        if (_tokens.TryGetValue(jobId, out var entry) && entry.ClaimHandle == claimHandle)
        {
            TryRemoveEntry(jobId, entry);
        }
    }

    /// <summary>
    /// Atomic compare-and-remove via <see cref="ICollection{T}.Remove(T)"/> on the
    /// concurrent dictionary. Only succeeds when the value at <paramref name="jobId"/>
    /// is still the same instance — if another thread replaced the entry between
    /// the caller's <c>TryGetValue</c> and this call, the removal is a no-op.
    /// </summary>
    private bool TryRemoveEntry(string jobId, CancellationEntry expectedEntry)
        => ((ICollection<KeyValuePair<string, CancellationEntry>>)_tokens)
            .Remove(new KeyValuePair<string, CancellationEntry>(jobId, expectedEntry));
}

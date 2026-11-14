// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.ControlPlane;

internal sealed class ExecutionJobCancellationTokens : IJobCancellationNotifier
{
    private sealed record CancellationEntry(CancellationTokenSource Cts, string ClaimHandle, int? AttemptCount);

    private readonly ConcurrentDictionary<string, CancellationEntry> _tokens = new(StringComparer.Ordinal);

    public CancellationTokenSource CreateLinkedTokenSource(string jobId, string claimHandle, params CancellationToken[] linkedTokens)
        => CreateLinkedTokenSource(jobId, (claimHandle, null), linkedTokens);

    public CancellationTokenSource CreateLinkedTokenSource(string jobId, (string Handle, int? AttemptCount) claim, params CancellationToken[] linkedTokens)
    {
        // CreateLinkedTokenSource rejects an empty token list. Callers that only need the
        // tracked source, with no host or timeout token, still have to be able to register one.
        var cts = linkedTokens is { Length: > 0 }
            ? CancellationTokenSource.CreateLinkedTokenSource(linkedTokens)
            : new CancellationTokenSource();
        var newEntry = new CancellationEntry(cts, claim.Handle, claim.AttemptCount);
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

    public bool Cancel(string jobId)
        => _tokens.TryGetValue(jobId, out var entry) && CancelEntry(entry);

    // Worker handles are reused across claims. A delayed request must match the
    // attempt as well as the handle, and cancel only the captured token source.
    private bool Cancel(ExecutionJobRecord job)
        => job.ClaimedBy != null
            && _tokens.TryGetValue(job.OperationId, out var entry)
            && entry.ClaimHandle == job.ClaimedBy
            && entry.AttemptCount == job.AttemptCount
            && CancelEntry(entry);

    public static bool CancelAttempt(IEnumerable<IJobCancellationNotifier> notifiers, ExecutionJobRecord job)
    {
        var signalled = false;
        foreach (var notifier in notifiers)
        {
            signalled |= notifier is ExecutionJobCancellationTokens tokens
                ? tokens.Cancel(job)
                : notifier.Cancel(job.OperationId);
        }

        return signalled;
    }

    private static bool CancelEntry(CancellationEntry entry)
    {
        try
        {
            entry.Cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The worker completed after the token lookup.
            return false;
        }
    }

    public void Revoke(string jobId, string claimHandle)
    {
        // Atomic compare-and-remove: only succeeds when the entry (CTS reference +
        // handle) still matches, preventing removal of a CTS registered by a new claim.
        // CancellationEntry is a record — equality includes CTS reference equality,
        // so a replacement entry with a different CTS instance will not match.
        if (_tokens.TryGetValue(jobId, out var entry) && entry.ClaimHandle == claimHandle && TryRemoveEntry(jobId, entry))
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

    public void Remove(string jobId, string claimHandle)
    {
        if (_tokens.TryGetValue(jobId, out var entry) && entry.ClaimHandle == claimHandle)
        {
            TryRemoveEntry(jobId, entry);
        }
    }

    private bool TryRemoveEntry(string jobId, CancellationEntry expectedEntry)
        => ((ICollection<KeyValuePair<string, CancellationEntry>>)_tokens)
            .Remove(new KeyValuePair<string, CancellationEntry>(jobId, expectedEntry));
}

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
internal sealed class ExecutionJobCancellationTokens : IJobCancellationNotifier
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a linked <see cref="CancellationTokenSource"/> for the job and tracks it.
    /// The returned source is linked to the supplied tokens so host shutdown and timeout
    /// also cancel job work.
    /// </summary>
    public CancellationTokenSource CreateLinkedTokenSource(string jobId, params CancellationToken[] linkedTokens)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedTokens);
        _tokens[jobId] = cts;
        return cts;
    }

    /// <inheritdoc />
    public bool Cancel(string jobId)
    {
        if (_tokens.TryGetValue(jobId, out var cts))
        {
            try
            {
                cts.Cancel();
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
    /// Removes the tracked token source after job completion or cancellation.
    /// </summary>
    public void Remove(string jobId)
    {
        _tokens.TryRemove(jobId, out _);
    }
}

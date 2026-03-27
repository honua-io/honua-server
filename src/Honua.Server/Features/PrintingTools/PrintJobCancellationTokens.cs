// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Server.Features.PrintingTools;

/// <summary>
/// Tracks per-job cancellation token sources for print jobs so that in-flight rendering
/// and upload work can be aborted when a job is cancelled via the admin API.
/// </summary>
internal sealed class PrintJobCancellationTokens : IJobCancellationNotifier
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a linked <see cref="CancellationTokenSource"/> for the job and tracks it.
    /// The returned source is linked to <paramref name="stoppingToken"/> so host shutdown
    /// also cancels job work.
    /// </summary>
    public CancellationTokenSource CreateLinkedTokenSource(string jobId, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _tokens[jobId] = cts;
        return cts;
    }

    /// <inheritdoc />
    public void Cancel(string jobId)
    {
        if (_tokens.TryGetValue(jobId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The background service disposed the CTS between our TryGetValue
                // and this Cancel call — the job already completed, so this is a no-op.
            }
        }
    }

    /// <summary>
    /// Removes the tracked token source after job completion or cancellation.
    /// </summary>
    public void Remove(string jobId)
    {
        _tokens.TryRemove(jobId, out _);
    }
}

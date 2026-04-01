// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Server.Features.Admin;

internal sealed class MigrationEvidenceCancellationTokens : IJobCancellationNotifier
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new(StringComparer.Ordinal);

    public CancellationTokenSource CreateLinkedTokenSource(string jobId, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _tokens[jobId] = cts;
        return cts;
    }

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
                return false;
            }
        }

        return false;
    }

    public void Remove(string jobId)
    {
        _tokens.TryRemove(jobId, out _);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Server.Features.PrintingTools;

internal sealed class PrintJobCancellationTokens : IJobCancellationNotifier
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
                // The background service disposed the CTS between our TryGetValue
                // and this Cancel call — the job already completed, so this is a no-op.
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

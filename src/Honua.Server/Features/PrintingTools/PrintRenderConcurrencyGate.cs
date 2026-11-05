// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.PrintingTools;

internal sealed class PrintRenderConcurrencyGate : IDisposable
{
    internal const string CapacityExceededMessage =
        "Print rendering capacity is currently exhausted. Retry later.";

    internal const int RetryAfterSeconds = 5;

    private const int MaxConcurrentSyncRenders = 2;

    private readonly SemaphoreSlim _gate = new(MaxConcurrentSyncRenders, MaxConcurrentSyncRenders);

    public async ValueTask<Lease?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new Lease(_gate);
    }

    public void Dispose() => _gate.Dispose();

    internal sealed class Lease(SemaphoreSlim gate) : IDisposable, IAsyncDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            gate.Release();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

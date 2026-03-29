// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.PrintingTools;

/// <summary>
/// Limits concurrent synchronous print render operations to prevent unbounded
/// Skia surface memory allocation.  Async jobs are already bounded by the
/// <see cref="System.Threading.Channels.Channel{T}"/> queue depth.
/// </summary>
/// <remarks>
/// Modelled after <c>MapServer.Rendering.RasterRenderCapacityLimiter</c> but
/// intentionally self-contained to preserve vertical-slice isolation.
/// </remarks>
internal sealed class PrintRenderConcurrencyGate : IDisposable
{
    internal const string CapacityExceededMessage =
        "Print rendering capacity is currently exhausted. Retry later.";

    internal const int RetryAfterSeconds = 5;

    private const int MaxConcurrentSyncRenders = 2;

    private readonly SemaphoreSlim _gate = new(MaxConcurrentSyncRenders, MaxConcurrentSyncRenders);

    /// <summary>
    /// Attempts a non-blocking acquire. Returns a disposable lease on success, null when at capacity.
    /// </summary>
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

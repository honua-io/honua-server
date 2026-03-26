// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.MapServer.Rendering;

/// <summary>
/// Limits concurrent raster render work and total reserved surface memory.
/// </summary>
internal sealed class RasterRenderCapacityLimiter : IDisposable
{
    internal const string CapacityExceededMessage = "Raster rendering capacity is currently exhausted. Retry later.";
    internal const int RetryAfterSeconds = 1;
    private const int BytesPerPixel = 4;
    private const int DefaultMaxConcurrentRenders = 4;
    private const long DefaultMaxReservedBytes = 256L * 1024L * 1024L;

    private readonly SemaphoreSlim _concurrencyGate;
    private readonly long _maxReservedBytes;
    private long _reservedBytes;

    public RasterRenderCapacityLimiter()
        : this(DefaultMaxConcurrentRenders, DefaultMaxReservedBytes)
    {
    }

    internal RasterRenderCapacityLimiter(int maxConcurrentRenders, long maxReservedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentRenders);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReservedBytes);

        _concurrencyGate = new SemaphoreSlim(maxConcurrentRenders, maxConcurrentRenders);
        _maxReservedBytes = maxReservedBytes;
    }

    public async ValueTask<Lease?> TryAcquireAsync(int width, int height, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var estimatedBytes = checked((long)width * height * BytesPerPixel);
        if (estimatedBytes > _maxReservedBytes)
        {
            return null;
        }

        if (!await _concurrencyGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (!TryReserve(estimatedBytes))
        {
            _concurrencyGate.Release();
            return null;
        }

        return new Lease(this, estimatedBytes);
    }

    private bool TryReserve(long estimatedBytes)
    {
        while (true)
        {
            var currentReservedBytes = Volatile.Read(ref _reservedBytes);
            var nextReservedBytes = currentReservedBytes + estimatedBytes;
            if (nextReservedBytes > _maxReservedBytes)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _reservedBytes, nextReservedBytes, currentReservedBytes) == currentReservedBytes)
            {
                return true;
            }
        }
    }

    private void Release(long estimatedBytes)
    {
        Interlocked.Add(ref _reservedBytes, -estimatedBytes);
        _concurrencyGate.Release();
    }

    public void Dispose()
    {
        _concurrencyGate.Dispose();
    }

    internal sealed class Lease : IDisposable, IAsyncDisposable
    {
        private readonly RasterRenderCapacityLimiter _owner;
        private readonly long _estimatedBytes;
        private int _disposed;

        public Lease(RasterRenderCapacityLimiter owner, long estimatedBytes)
        {
            _owner = owner;
            _estimatedBytes = estimatedBytes;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.Release(_estimatedBytes);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

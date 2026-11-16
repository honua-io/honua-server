// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Providers;

public sealed class RoutingSolveCapacityLimiter : IDisposable
{
    public const int DefaultMaxConcurrentSolves = 2;

    public const int RetryAfterSeconds = 1;

    public const string CapacityExceededMessage = "Routing solve capacity is currently exhausted. Retry later.";

    private static readonly TimeSpan DefaultAcquireTimeout = TimeSpan.FromSeconds(RetryAfterSeconds);

    private readonly SemaphoreSlim _gate;
    private readonly TimeSpan _acquireTimeout;

    public RoutingSolveCapacityLimiter()
        : this(DefaultMaxConcurrentSolves, DefaultAcquireTimeout)
    {
    }

    public RoutingSolveCapacityLimiter(int maxConcurrentSolves, TimeSpan acquireTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentSolves);
        ArgumentOutOfRangeException.ThrowIfLessThan(acquireTimeout, TimeSpan.Zero);

        _gate = new SemaphoreSlim(maxConcurrentSolves, maxConcurrentSolves);
        _acquireTimeout = acquireTimeout;
    }

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(_acquireTimeout, cancellationToken).ConfigureAwait(false))
        {
            throw new RoutingSolveCapacityExceededException(CapacityExceededMessage);
        }

        return new Lease(_gate);
    }

    public void Dispose() => _gate.Dispose();

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
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
    }
}

public sealed class RoutingSolveCapacityExceededException(string message) : Exception(message);

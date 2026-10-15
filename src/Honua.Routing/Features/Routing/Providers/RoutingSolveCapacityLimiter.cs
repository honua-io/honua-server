// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Process-wide gate on concurrent pgRouting solves. Anonymous NAServer and
/// geoprocessing callers share the same provider, so the gate lives next to the
/// solve rather than on one HTTP adapter.
/// </summary>
public sealed class RoutingSolveCapacityLimiter : IDisposable
{
    /// <summary>Default number of pgRouting solves admitted at once.</summary>
    public const int DefaultMaxConcurrentSolves = 2;

    /// <summary>Seconds a rejected caller should wait before retrying.</summary>
    public const int RetryAfterSeconds = 1;

    /// <summary>Detail returned when a solve cannot acquire a slot.</summary>
    public const string CapacityExceededMessage = "Routing solve capacity is currently exhausted. Retry later.";

    private static readonly TimeSpan DefaultAcquireTimeout = TimeSpan.FromSeconds(RetryAfterSeconds);

    private readonly SemaphoreSlim _gate;
    private readonly TimeSpan _acquireTimeout;

    /// <summary>
    /// Initializes a limiter with <see cref="DefaultMaxConcurrentSolves"/> and a one-second acquire timeout.
    /// </summary>
    public RoutingSolveCapacityLimiter()
        : this(DefaultMaxConcurrentSolves, DefaultAcquireTimeout)
    {
    }

    /// <summary>
    /// Initializes a limiter with an explicit slot count and acquire timeout.
    /// </summary>
    /// <param name="maxConcurrentSolves">Slots admitted at once. Must be positive.</param>
    /// <param name="acquireTimeout">How long a caller waits for a slot. Zero fails immediately when the gate is full.</param>
    public RoutingSolveCapacityLimiter(int maxConcurrentSolves, TimeSpan acquireTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentSolves);
        ArgumentOutOfRangeException.ThrowIfLessThan(acquireTimeout, TimeSpan.Zero);

        _gate = new SemaphoreSlim(maxConcurrentSolves, maxConcurrentSolves);
        _acquireTimeout = acquireTimeout;
    }

    /// <summary>
    /// Acquires a solve slot. The caller must dispose the lease when the solve finishes.
    /// </summary>
    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(_acquireTimeout, cancellationToken).ConfigureAwait(false))
        {
            throw new RoutingSolveCapacityExceededException(CapacityExceededMessage);
        }

        return new Lease(_gate);
    }

    /// <summary>Releases the gate.</summary>
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

/// <summary>
/// Raised when <see cref="RoutingSolveCapacityLimiter"/> cannot admit another solve.
/// Adapters map this to HTTP 503.
/// </summary>
/// <param name="message">The error message.</param>
public sealed class RoutingSolveCapacityExceededException(string message) : Exception(message);

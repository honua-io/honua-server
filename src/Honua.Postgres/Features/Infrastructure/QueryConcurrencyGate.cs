// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// Singleton admission-control gate that limits concurrent database operations
/// using a <see cref="SemaphoreSlim"/> backed by <see cref="ConnectionLimits.MaxConcurrentQueries"/>.
/// </summary>
internal sealed class QueryConcurrencyGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _acquisitionTimeout;

    public QueryConcurrencyGate(ConnectionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _semaphore = new SemaphoreSlim(limits.MaxConcurrentQueries, limits.MaxConcurrentQueries);
        _acquisitionTimeout = TimeSpan.FromSeconds(limits.ConnectionAcquisitionTimeoutSeconds);
    }

    /// <summary>
    /// Waits for an available slot. Returns <see langword="false"/> if the timeout expires.
    /// </summary>
    public async Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        return await _semaphore.WaitAsync(_acquisitionTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases one or more slots back to the gate.
    /// </summary>
    public void Release(int count = 1)
    {
        if (count > 0)
        {
            _semaphore.Release(count);
        }
    }

    /// <summary>
    /// Number of slots currently available (for diagnostics/telemetry).
    /// </summary>
    public int AvailableSlots => _semaphore.CurrentCount;

    /// <inheritdoc/>
    public void Dispose()
    {
        _semaphore.Dispose();
    }
}

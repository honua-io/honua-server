// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Linq;
using Honua.Core.Configuration;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// Singleton admission-control gate that limits concurrent database operations.
/// </summary>
internal sealed class QueryConcurrencyGate : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<QueuedWaiter> _waiters = new();
    private readonly TimeSpan _acquisitionTimeout;
    private readonly bool _adaptiveEnabled;
    private readonly int _minLimit;
    private readonly int _maxLimit;
    private readonly double _targetDurationMs;
    private readonly long _updateIntervalTicks;
    private int _targetLimit;
    private int _inFlight;
    private long _lastAdjustmentTimestamp;
    private double _durationEwmaMs;
    private double _queueWaitEwmaMs;
    private long _adjustmentCount;
    private int _lastAdjustmentDirection;
    private bool _disposed;

    public QueryConcurrencyGate(ConnectionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var configuredPoolCeiling = limits.MaxConnectionPoolSize > 0
            ? limits.MaxConnectionPoolSize
            : limits.MaxConcurrentQueries;

        _adaptiveEnabled = limits.AdaptiveConcurrencyEnabled;
        var hardCeiling = Math.Max(1, Math.Min(limits.MaxConcurrentQueries, configuredPoolCeiling));
        _maxLimit = _adaptiveEnabled && limits.AdaptiveConcurrencyMaxQueries > 0
            ? Clamp(limits.AdaptiveConcurrencyMaxQueries, 1, hardCeiling)
            : hardCeiling;
        _minLimit = _adaptiveEnabled
            ? Clamp(limits.AdaptiveConcurrencyMinQueries, 1, _maxLimit)
            : _maxLimit;
        _targetLimit = _adaptiveEnabled && limits.AdaptiveConcurrencyInitialQueries > 0
            ? Clamp(limits.AdaptiveConcurrencyInitialQueries, _minLimit, _maxLimit)
            : _maxLimit;
        _targetDurationMs = Math.Max(1, limits.AdaptiveConcurrencyTargetDurationMs);
        _updateIntervalTicks = MillisecondsToStopwatchTicks(limits.AdaptiveConcurrencyUpdateIntervalMs);
        _lastAdjustmentTimestamp = Stopwatch.GetTimestamp();
        _acquisitionTimeout = TimeSpan.FromSeconds(limits.ConnectionAcquisitionTimeoutSeconds);
    }

    /// <summary>
    /// Acquisition timeout in whole seconds, used to populate the
    /// <c>Retry-After</c> hint when a 503 is returned for a gate timeout.
    /// </summary>
    public int AcquisitionTimeoutSeconds => (int)Math.Max(1, Math.Ceiling(_acquisitionTimeout.TotalSeconds));

    /// <summary>
    /// Waits for an available slot. Returns <see langword="false"/> if the timeout expires.
    /// </summary>
    public async Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        QueuedWaiter waiter;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_waiters.Count == 0 && _inFlight < _targetLimit)
            {
                _inFlight++;
                return true;
            }

            waiter = new QueuedWaiter(
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                Stopwatch.GetTimestamp());
            _waiters.Enqueue(waiter);
        }

        try
        {
            return await waiter.Completion.Task.WaitAsync(_acquisitionTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (waiter.Completion.TrySetCanceled(CancellationToken.None))
            {
                RemoveWaiter(waiter);
                return false;
            }

            return await waiter.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!waiter.Completion.TrySetCanceled(cancellationToken))
            {
                return await waiter.Completion.Task.ConfigureAwait(false);
            }

            RemoveWaiter(waiter);
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>
    /// Removes a timed-out/cancelled waiter from the queue so its abandoned slot request
    /// no longer inflates <c>_waiters.Count</c> (which drives adaptive-concurrency pressure
    /// decisions and telemetry) or blocks the fast-path admission check. A no-op if a
    /// concurrent drain already dequeued it. Cancellations are rare, so the O(n) rebuild
    /// of the FIFO queue is acceptable.
    /// </summary>
    private void RemoveWaiter(QueuedWaiter waiter)
    {
        lock (_sync)
        {
            if (_waiters.Count == 0)
            {
                return;
            }

            var retained = _waiters.Where(w => !ReferenceEquals(w, waiter)).ToArray();
            if (retained.Length == _waiters.Count)
            {
                return;
            }

            _waiters.Clear();
            foreach (var w in retained)
            {
                _waiters.Enqueue(w);
            }
        }
    }

    /// <summary>
    /// Releases one or more slots back to the gate.
    /// </summary>
    public void Release(int count = 1)
    {
        ReleaseCore(count, leaseDuration: null);
    }

    /// <summary>
    /// Releases a slot and records how long the database lease was held.
    /// </summary>
    public void Release(TimeSpan leaseDuration)
    {
        ReleaseCore(1, leaseDuration);
    }

    /// <summary>
    /// Adjusts the current logical admission target. Intended for tests and
    /// future external controllers; the configured pool ceiling remains fixed.
    /// </summary>
    internal void SetTargetLimit(int targetLimit)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SetTargetLimitLocked(targetLimit);
            DrainWaitersLocked();
        }
    }

    /// <summary>
    /// Current logical concurrent query limit.
    /// </summary>
    public int CurrentLimit
    {
        get
        {
            lock (_sync)
            {
                return _targetLimit;
            }
        }
    }

    /// <summary>
    /// Hard ceiling for the logical gate, clamped to the configured Npgsql pool max.
    /// </summary>
    public int MaxLimit => _maxLimit;

    /// <summary>
    /// Number of slots currently available (for diagnostics/telemetry).
    /// </summary>
    public int AvailableSlots
    {
        get
        {
            lock (_sync)
            {
                return Math.Max(0, _targetLimit - _inFlight);
            }
        }
    }

    internal QueryConcurrencyGateSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new QueryConcurrencyGateSnapshot(
                _adaptiveEnabled,
                _targetLimit,
                _minLimit,
                _maxLimit,
                _inFlight,
                Math.Max(0, _targetLimit - _inFlight),
                _waiters.Count,
                _targetDurationMs,
                _durationEwmaMs,
                _queueWaitEwmaMs,
                _adjustmentCount,
                FormatAdjustmentDirection(_lastAdjustmentDirection));
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        QueuedWaiter[] waiters;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            waiters = _waiters.ToArray();
            _waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetException(new ObjectDisposedException(nameof(QueryConcurrencyGate)));
        }
    }

    private void ReleaseCore(int count, TimeSpan? leaseDuration)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var wasSaturated = _inFlight >= _targetLimit;
            _inFlight = Math.Max(0, _inFlight - count);

            if (leaseDuration.HasValue)
            {
                AdjustLimitLocked(leaseDuration.Value, wasSaturated);
            }

            DrainWaitersLocked();
        }
    }

    private void AdjustLimitLocked(TimeSpan leaseDuration, bool wasSaturated)
    {
        if (!_adaptiveEnabled)
        {
            return;
        }

        var durationMs = Math.Max(0, leaseDuration.TotalMilliseconds);
        _durationEwmaMs = _durationEwmaMs <= 0
            ? durationMs
            : (_durationEwmaMs * 0.8) + (durationMs * 0.2);

        var now = Stopwatch.GetTimestamp();
        if (_updateIntervalTicks > 0 && now - _lastAdjustmentTimestamp < _updateIntervalTicks)
        {
            return;
        }

        _lastAdjustmentTimestamp = now;

        var highWatermark = _targetDurationMs * 1.25;
        var queuePressureWatermark = _targetDurationMs * 0.5;
        var severeHighWatermark = _targetDurationMs * 3.0;
        var queuedWaiters = _waiters.Count;
        var queuePressureDominates = queuedWaiters >= _targetLimit || _queueWaitEwmaMs > queuePressureWatermark;
        var databaseLeaseIsSevere = _durationEwmaMs > severeHighWatermark;
        if (_durationEwmaMs > highWatermark &&
            _targetLimit > _minLimit &&
            (!queuePressureDominates || databaseLeaseIsSevere))
        {
            SetTargetLimitLocked(Math.Max(_minLimit, (int)Math.Floor(_targetLimit * 0.85)));
            return;
        }

        var lowWatermark = _targetDurationMs * 0.75;
        if (wasSaturated && _durationEwmaMs < lowWatermark && _targetLimit < _maxLimit)
        {
            SetTargetLimitLocked(_targetLimit + Math.Max(1, (int)Math.Sqrt(_targetLimit)));
        }
    }

    private void SetTargetLimitLocked(int targetLimit)
    {
        var nextLimit = Clamp(targetLimit, _minLimit, _maxLimit);
        if (nextLimit == _targetLimit)
        {
            return;
        }

        _lastAdjustmentDirection = nextLimit > _targetLimit ? 1 : -1;
        _targetLimit = nextLimit;
        _adjustmentCount++;
    }

    private void DrainWaitersLocked()
    {
        while (_inFlight < _targetLimit && _waiters.Count > 0)
        {
            var waiter = _waiters.Dequeue();
            if (waiter.Completion.TrySetResult(true))
            {
                RecordQueueWaitLocked(waiter.EnqueuedAtTimestamp);
                _inFlight++;
            }
        }
    }

    private void RecordQueueWaitLocked(long enqueuedAtTimestamp)
    {
        var elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - enqueuedAtTimestamp);
        var waitMs = elapsedTicks * 1000d / Stopwatch.Frequency;
        _queueWaitEwmaMs = _queueWaitEwmaMs <= 0
            ? waitMs
            : (_queueWaitEwmaMs * 0.8) + (waitMs * 0.2);
    }

    private static int Clamp(int value, int min, int max)
        => Math.Min(max, Math.Max(min, value));

    private static string FormatAdjustmentDirection(int direction)
        => direction switch
        {
            > 0 => "increase",
            < 0 => "decrease",
            _ => "none"
        };

    private static long MillisecondsToStopwatchTicks(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            return 0;
        }

        return (long)(Stopwatch.Frequency * (milliseconds / 1000d));
    }
}

internal sealed record QueuedWaiter(
    TaskCompletionSource<bool> Completion,
    long EnqueuedAtTimestamp);

internal readonly record struct QueryConcurrencyGateSnapshot(
    bool AdaptiveEnabled,
    int CurrentLimit,
    int MinLimit,
    int MaxLimit,
    int InFlight,
    int AvailableSlots,
    int QueuedWaiters,
    double TargetDurationMs,
    double DurationEwmaMs,
    double QueueWaitEwmaMs,
    long AdjustmentCount,
    string LastAdjustmentDirection);

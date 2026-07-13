// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Runtime-tunable bounded database admission gate. The configured admission
/// limits are captured at construction time; this seam exposes a bounded-range,
/// transient override so a control-plane ops action can relieve database pressure
/// by lowering the concurrent-query admission target without a restart. The
/// override is never persisted: on process restart the gate reverts to its
/// configured values.
/// </summary>
public interface IRuntimeTunableAdmissionGate
{
    /// <summary>
    /// Current logical concurrent-query admission target.
    /// </summary>
    int CurrentLimit { get; }

    /// <summary>
    /// Hard ceiling for the admission target. A tune request may not exceed this
    /// value (the gate can never admit more than the configured pool can serve).
    /// </summary>
    int MaxLimit { get; }

    /// <summary>
    /// Lowest admission target a tune request may set (inclusive).
    /// </summary>
    int MinLimit { get; }

    /// <summary>
    /// Attempts to set a new transient admission target, validated against the
    /// bounded range <c>[<see cref="MinLimit"/>, <see cref="MaxLimit"/>]</c>. The
    /// value is not persisted and reverts to the configured limit on restart.
    /// </summary>
    /// <param name="limit">Requested admission target.</param>
    /// <param name="error">Human-readable rejection reason when the value is out of range.</param>
    /// <returns>True when the target was applied; false when <paramref name="limit"/> is out of range.</returns>
    bool TrySetLimit(int limit, out string? error);

    /// <summary>
    /// Reads a live database-admission pressure snapshot sourced from the gate's real throttle state
    /// (in-flight leases, queued waiters, queue-wait EWMA, and a WINDOWED acquisition-timeout count). This is
    /// the production signal behind the <c>db-bounded-admission-pressure</c> ops finding (#2805): pressure is
    /// derived from the actual admission path rather than counters that had no production writer.
    /// </summary>
    /// <returns>The current admission pressure snapshot.</returns>
    DatabaseAdmissionPressure GetPressure();
}

/// <summary>
/// Point-in-time database-admission pressure derived from the concurrency gate (#2805). Utilization and the
/// queue signals are instantaneous; <see cref="WindowedAcquisitionTimeouts"/> is a trailing-window count so
/// the finding reflects a current exhaustion episode rather than a lifetime-monotonic total that never clears.
/// </summary>
/// <param name="InFlight">Number of admission slots currently leased (queries executing).</param>
/// <param name="CurrentLimit">Current logical admission target (the effective bound on concurrency).</param>
/// <param name="QueuedWaiters">Number of callers currently queued waiting for a slot.</param>
/// <param name="QueueWaitEwmaMs">Exponentially-weighted moving average of queue-wait time, in milliseconds.</param>
/// <param name="WindowedAcquisitionTimeouts">Acquisition timeouts observed within the trailing pressure window.</param>
public readonly record struct DatabaseAdmissionPressure(
    int InFlight,
    int CurrentLimit,
    int QueuedWaiters,
    double QueueWaitEwmaMs,
    long WindowedAcquisitionTimeouts)
{
    /// <summary>Gets a value indicating whether a utilization ratio can be computed (the limit is known and positive).</summary>
    public bool HasUtilization => CurrentLimit > 0;

    /// <summary>
    /// Gets the admission utilization ratio (in-flight over the current limit, clamped to at least 0). Reaches
    /// 1.0 when every slot is leased; queued waiters on top of that indicate saturation beyond the limit.
    /// </summary>
    public double Utilization => CurrentLimit > 0 ? (double)InFlight / CurrentLimit : 0d;
}

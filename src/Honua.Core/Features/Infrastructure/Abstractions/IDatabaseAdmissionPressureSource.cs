// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Read-only source of genuine database admission-pressure signals, backed by the
/// concurrency/admission gate that actually throttles connection acquisition on the hot
/// path. Exposed as a Core abstraction (rather than a provider internal) so server-side
/// observability — the ops-findings <c>db-bounded-admission-pressure</c> rule and the
/// ops-health snapshot — can read real saturation and timeout signals without taking a
/// compile-time dependency on provider internals.
/// </summary>
/// <remarks>
/// The prior implementation read decorative counters that no production code ever wrote
/// (<c>ConnectionPoolMetrics.UpdatePoolSize/RecordConnectionTimeout</c>), so the pressure
/// rule could never fire and the health snapshot reported <c>utilization: null, timeouts: 0</c>
/// during a real pool-exhaustion incident (honua-server#2805). This seam wires those reads
/// to the gate that is the actual admission-control chokepoint.
/// </remarks>
public interface IDatabaseAdmissionPressureSource
{
    /// <summary>Reads the current database admission-pressure snapshot.</summary>
    /// <returns>A point-in-time <see cref="DatabaseAdmissionPressureReading"/>.</returns>
    DatabaseAdmissionPressureReading GetPressureReading();
}

/// <summary>
/// A single point-in-time database admission-pressure reading.
/// </summary>
/// <param name="HasUtilization">
/// Whether logical admission utilization is available (true whenever an admission gate is
/// active, since the gate always carries a positive target limit).
/// </param>
/// <param name="Utilization">
/// Logical admission utilization ratio (0.0-1.0): in-flight admissions divided by the
/// current admission target. A saturated gate with queued waiters reads <c>1.0</c>.
/// </param>
/// <param name="InFlight">Number of admissions currently in flight.</param>
/// <param name="CurrentLimit">Current logical admission target (concurrent-query limit).</param>
/// <param name="QueuedWaiters">Number of callers currently queued waiting for a slot.</param>
/// <param name="AcquisitionTimeoutsInWindow">
/// Count of connection-acquisition timeouts observed within the gate's rolling window. This
/// is a windowed signal, not a lifetime-monotonic total, so a burst of timeouts relaxes back
/// to zero once the window elapses instead of latching until process restart.
/// </param>
public readonly record struct DatabaseAdmissionPressureReading(
    bool HasUtilization,
    double Utilization,
    int InFlight,
    int CurrentLimit,
    int QueuedWaiters,
    long AcquisitionTimeoutsInWindow);

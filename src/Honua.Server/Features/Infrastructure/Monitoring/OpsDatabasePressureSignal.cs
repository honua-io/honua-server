// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Abstractions;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Point-in-time database connection-pool pressure signal consumed by the
/// <c>db-bounded-admission-pressure</c> ops-findings rule. Abstracted behind an interface (rather than
/// depending on <see cref="ProductionMetricsCollector"/> directly) so the rule can be unit-tested with a
/// fake seam, matching the pattern already used for alert-dispatch health and deploy preflight.
/// </summary>
internal interface IOpsDatabasePressureSignal
{
    /// <summary>Reads the current database connection-pool pressure snapshot.</summary>
    DatabasePressureSnapshot GetSnapshot();
}

/// <summary>A single database connection-pool pressure reading.</summary>
/// <param name="HasConnectionPoolUtilization">Whether pool utilization telemetry is available.</param>
/// <param name="ConnectionPoolUtilization">Connection-pool utilization ratio (0.0-1.0), when available.</param>
/// <param name="ConnectionAcquisitionTimeouts">Connection-acquisition timeout count over the pressure window.</param>
/// <param name="ConnectionAcquisitionFailures">Connection-acquisition failure count.</param>
/// <param name="QueuedWaiters">Number of callers currently queued waiting for an admission slot.</param>
internal readonly record struct DatabasePressureSnapshot(
    bool HasConnectionPoolUtilization,
    double ConnectionPoolUtilization,
    long ConnectionAcquisitionTimeouts,
    long ConnectionAcquisitionFailures,
    int QueuedWaiters = 0);

/// <summary>
/// Default <see cref="IOpsDatabasePressureSignal"/> backed by the shared <see cref="ProductionMetricsCollector"/>
/// singleton (the same source the ops-health snapshot's database view reads).
/// </summary>
internal sealed class ProductionMetricsDatabasePressureSignal(ProductionMetricsCollector metricsCollector)
    : IOpsDatabasePressureSignal
{
    public DatabasePressureSnapshot GetSnapshot()
    {
        var metrics = metricsCollector.GetHealthMetrics();
        return new DatabasePressureSnapshot(
            metrics.HasDatabaseConnectionPoolUtilization,
            metrics.DatabaseConnectionPoolUtilization,
            metrics.ConnectionAcquisitionTimeouts,
            metrics.ConnectionAcquisitionFailures);
    }
}

/// <summary>
/// <see cref="IOpsDatabasePressureSignal"/> sourced from the real database admission gate (#2805). The
/// <c>db-bounded-admission-pressure</c> finding previously read <see cref="ProductionMetricsCollector"/>
/// counters that had NO production writer (utilization was always "unavailable" and the timeout/failure
/// totals were always zero), so it could never fire during a real exhaustion. The gate is the actual
/// throttle in <c>CachingDatabaseConnectionProvider.OpenConnectionAsync</c>: it knows the live in-flight
/// count, the current admission limit, the queued waiters, and a WINDOWED acquisition-timeout count.
/// </summary>
internal sealed class AdmissionGateDatabasePressureSignal(IRuntimeTunableAdmissionGate admissionGate)
    : IOpsDatabasePressureSignal
{
    public DatabasePressureSnapshot GetSnapshot()
    {
        var pressure = admissionGate.GetPressure();
        return new DatabasePressureSnapshot(
            pressure.HasUtilization,
            pressure.Utilization,
            pressure.WindowedAcquisitionTimeouts,
            0,
            pressure.QueuedWaiters);
    }
}

/// <summary>
/// Bundles the ops-findings engine's data-source collaborators that are ONLY wired in certain
/// topologies (Postgres-only telemetry, the durable-backend-gated rollup store) behind one
/// constructor parameter. <see cref="OpsFindingsService"/> already carries the durable
/// control-plane collaborators (gateway/workflow store/job store) plus several always-present
/// seams; collapsing this group into a snapshot keeps the constructor under the
/// architecture-enforced collaborator ceiling (<c>DependencyInjectionLimitsTests</c>) instead of
/// growing it per new rule.
/// </summary>
internal sealed class OpsFindingsExtendedSignals
{
    /// <summary>Database connection-pool pressure signal (Postgres only), or null when unavailable.</summary>
    public IOpsDatabasePressureSignal? DatabasePressureSignal { get; init; }

    /// <summary>Runtime-tunable bounded database admission gate (Postgres only), or null when unavailable.</summary>
    public IRuntimeTunableAdmissionGate? AdmissionGate { get; init; }

    /// <summary>Persisted ops-health rollup store (#2553, Postgres only), or null when unavailable.</summary>
    public IOpsHealthRollupStore? RollupStore { get; init; }
}

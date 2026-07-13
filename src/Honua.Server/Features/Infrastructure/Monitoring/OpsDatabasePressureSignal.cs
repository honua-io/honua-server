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
/// <param name="ConnectionAcquisitionTimeouts">Total connection-acquisition timeout count.</param>
/// <param name="ConnectionAcquisitionFailures">Total connection-acquisition failure count.</param>
internal readonly record struct DatabasePressureSnapshot(
    bool HasConnectionPoolUtilization,
    double ConnectionPoolUtilization,
    long ConnectionAcquisitionTimeouts,
    long ConnectionAcquisitionFailures);

/// <summary>
/// Primary <see cref="IOpsDatabasePressureSignal"/> backed by the live
/// <see cref="IDatabaseAdmissionPressureSource"/> — the admission gate that actually throttles
/// connection acquisition on the hot path. This is the genuine sensor the
/// <c>db-bounded-admission-pressure</c> rule keys on; prior to honua-server#2805 the rule read
/// counters that no production code ever wrote, so it could never fire during a real
/// pool-exhaustion incident.
/// </summary>
internal sealed class AdmissionGateDatabasePressureSignal(IDatabaseAdmissionPressureSource source)
    : IOpsDatabasePressureSignal
{
    public DatabasePressureSnapshot GetSnapshot()
    {
        var reading = source.GetPressureReading();
        return new DatabasePressureSnapshot(
            reading.HasUtilization,
            reading.Utilization,
            reading.AcquisitionTimeoutsInWindow,
            // Non-timeout acquisition failures are not separately instrumented in the
            // admission-gate model; timeouts are the actionable pressure signal.
            ConnectionAcquisitionFailures: 0);
    }
}

/// <summary>
/// Fallback <see cref="IOpsDatabasePressureSignal"/> backed by the shared
/// <see cref="ProductionMetricsCollector"/> singleton, used only when no admission gate is
/// registered (providers without a bounded admission gate). The collector's pool view is itself
/// gate-backed, so this honestly reports utilization as unavailable rather than fabricating it.
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

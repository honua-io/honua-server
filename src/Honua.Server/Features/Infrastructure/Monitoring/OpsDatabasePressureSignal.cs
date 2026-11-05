// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Abstractions;

namespace Honua.Infrastructure.Monitoring;

internal interface IOpsDatabasePressureSignal
{
    DatabasePressureSnapshot GetSnapshot();
}

internal readonly record struct DatabasePressureSnapshot(
    bool HasConnectionPoolUtilization,
    double ConnectionPoolUtilization,
    long ConnectionAcquisitionTimeouts,
    long ConnectionAcquisitionFailures,
    int QueuedWaiters = 0);

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

// The pressure reading comes from the admission gate, which has the live in-flight count and the windowed timeout count.
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

internal sealed class OpsFindingsExtendedSignals
{
    public IManagedComposeObserver? ManagedComposeObserver { get; init; }

    public IOpsDatabasePressureSignal? DatabasePressureSignal { get; init; }

    public IRuntimeTunableAdmissionGate? AdmissionGate { get; init; }

    public IOpsHealthRollupStore? RollupStore { get; init; }

    public OpsFindingsCollectionLedger? CollectionLedger { get; init; }

    public TimeProvider? TimeProvider { get; init; }
}

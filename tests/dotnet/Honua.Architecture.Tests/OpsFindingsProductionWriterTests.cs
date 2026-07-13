// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Recurrence guard for #2805: every metric a deterministic ops-findings rule READS must have a real
/// production WRITER. The <c>db-bounded-admission-pressure</c> rule previously read
/// <see cref="ConnectionPoolMetrics"/> counters (pool utilization, acquisition timeouts/failures) that had
/// no production caller — <c>UpdatePoolSize</c>/<c>RecordConnectionTimeout</c>/
/// <c>RecordConnectionAcquisitionFailure</c> were dead — so the finding could never fire during a real
/// exhaustion. These structural assertions fail if the pressure signal is ever reverted to a source with no
/// writer, so a read-without-writer regression is caught at build time rather than in production silence.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class OpsFindingsProductionWriterTests
{
    private const string AdmissionGateSignalTypeName = "Honua.Infrastructure.Monitoring.AdmissionGateDatabasePressureSignal";
    private const string PressureSignalInterfaceName = "Honua.Infrastructure.Monitoring.IOpsDatabasePressureSignal";
    private const string GateTypeName = "Honua.Postgres.Features.Infrastructure.QueryConcurrencyGate";
    private const string ConnectionProviderTypeName = "Honua.Postgres.Features.Infrastructure.Caching.CachingDatabaseConnectionProvider";

    [Fact]
    public void DatabasePressureFinding_SignalIsBackedByTheAdmissionGateWriter()
    {
        // Anchor the Honua.Server assembly via a public type it owns so the load never depends on
        // simple-name probing.
        var serverAssembly = typeof(Honua.Infrastructure.Monitoring.OpsFindingsOptions).Assembly;

        var signalType = serverAssembly.GetType(AdmissionGateSignalTypeName);
        signalType.Should().NotBeNull(
            "the db-bounded-admission-pressure finding must read pressure from the live admission gate, not a dead-counter source");

        signalType!.GetInterfaces().Should().Contain(
            i => i.FullName == PressureSignalInterfaceName,
            "the gate-backed signal must implement the findings pressure-signal seam");

        // The signal's collaborator must be the admission gate — the production writer of pressure — so the
        // metric the finding reads is provably fed by real throttle state.
        var acceptsAdmissionGate = signalType.GetConstructors()
            .SelectMany(ctor => ctor.GetParameters())
            .Any(p => p.ParameterType == typeof(IRuntimeTunableAdmissionGate));
        acceptsAdmissionGate.Should().BeTrue(
            "AdmissionGateDatabasePressureSignal must source pressure from IRuntimeTunableAdmissionGate");
    }

    [Fact]
    public void AdmissionGate_ExposesPressureAndIsTheRuntimeTunableGate()
    {
        var gateAssembly = Assembly.Load("Honua.Postgres.Shared");

        var gateType = gateAssembly.GetType(GateTypeName);
        gateType.Should().NotBeNull("the concurrency gate is the production writer of admission pressure");

        typeof(IRuntimeTunableAdmissionGate).IsAssignableFrom(gateType).Should().BeTrue(
            "the gate must be the runtime-tunable admission gate the pressure signal consumes");

        gateType!.GetMethod(nameof(IRuntimeTunableAdmissionGate.GetPressure)).Should().NotBeNull(
            "the gate must expose the pressure snapshot the finding reads");
    }

    [Fact]
    public void ConnectionPoolMetricsWriters_AreWiredIntoTheConnectionProvider()
    {
        // The previously-dead ConnectionPoolMetrics counters are now written by the connection provider so
        // the database health check reports real utilization/timeouts. Assert the wiring exists (the provider
        // takes ConnectionPoolMetrics) so the counters cannot silently return to being unwritten.
        var gateAssembly = Assembly.Load("Honua.Postgres.Shared");
        var providerType = gateAssembly.GetType(ConnectionProviderTypeName);
        providerType.Should().NotBeNull();

        var wiresPoolMetrics = providerType!.GetConstructors()
            .SelectMany(ctor => ctor.GetParameters())
            .Any(p => p.ParameterType == typeof(ConnectionPoolMetrics));
        wiresPoolMetrics.Should().BeTrue(
            "CachingDatabaseConnectionProvider must receive ConnectionPoolMetrics so its counters have a production writer");
    }
}

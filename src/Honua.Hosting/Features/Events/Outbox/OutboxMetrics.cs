// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;

namespace Honua.Infrastructure.Events.Outbox;

internal sealed class OutboxMetrics
{
    private readonly Meter _meter;
    private readonly Counter<long> _dispatched;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _deadLettered;
    private readonly Counter<long> _recoveredClaims;

    private long _pendingCount;
    private long _deadLetteredCount;
    private double _oldestPendingAgeSeconds;

    public OutboxMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        // Instruments use the shared Honua meter because a differently named meter needs exporter registration.
        _meter = meterFactory.Create(HonuaTelemetry.ServiceName, HonuaTelemetry.ServiceVersion);

        // `unit: null` on the instruments below is deliberate, not an oversight. The OpenTelemetry
        // Prometheus exporter derives the exported series name from the instrument name AND its unit:
        // it maps the unit through the UCUM table and appends it, so a declared unit renames the series
        // out from under every dashboard and alert rule without breaking a single build. A PromQL query
        // against the absent name returns an empty vector, so the panel is blank and the alert never
        // fires, silently. Units are documented in the instrument name and description instead. See the
        // SLO-contract comment block in HonuaTelemetry.cs, eng/telemetry/metric-name-contract.v1.json, and
        // MetricNameContractTests, which scrapes the real /metrics exposition and fails on drift.

        _dispatched = _meter.CreateCounter<long>(
            "honua.outbox.dispatched_total",
            unit: null,
            "Feature-change outbox rows successfully dispatched to the canonical event publisher.");

        _failed = _meter.CreateCounter<long>(
            "honua.outbox.failed_total",
            unit: null,
            "Feature-change outbox rows whose dispatch attempt failed (still retriable until dead-lettered).");

        _deadLettered = _meter.CreateCounter<long>(
            "honua.outbox.dead_lettered_total",
            unit: null,
            "Feature-change outbox rows that exhausted retries and require operator triage.");

        _recoveredClaims = _meter.CreateCounter<long>(
            "honua.outbox.recovered_claims_total",
            unit: null,
            "Outbox claim leases reset by the recovery loop after a worker exit.");

        // Observable gauges report the dispatcher's most recent backlog snapshot.
        // The dispatcher refreshes the backing fields after each dispatch loop, so
        // these gauges read the cached values without re-querying the database on
        // every Prometheus scrape.
        _meter.CreateObservableGauge(
            "honua.outbox.pending_count",
            () => Interlocked.Read(ref _pendingCount),
            unit: null,
            description: "Outbox rows waiting for dispatch (pending, claimed, or retriable).");

        _meter.CreateObservableGauge(
            "honua.outbox.dead_lettered_count",
            () => Interlocked.Read(ref _deadLetteredCount),
            unit: null,
            description: "Outbox rows currently in the dead-letter state.");

        _meter.CreateObservableGauge(
            "honua.outbox.oldest_pending_age_seconds",
            () => Volatile.Read(ref _oldestPendingAgeSeconds),
            unit: "seconds",
            description: "Age in seconds of the oldest backlog row (pending, failed, or claimed).");
    }

    public Meter Meter => _meter;

    public void RecordDispatched() => _dispatched.Add(1);

    public void RecordFailed() => _failed.Add(1);

    public void RecordDeadLettered() => _deadLettered.Add(1);

    public void RecordRecoveredClaims(long count)
    {
        if (count > 0)
        {
            _recoveredClaims.Add(count);
        }
    }

    public void RecordBacklog(long pending, long deadLettered, double oldestPendingAgeSeconds)
    {
        Interlocked.Exchange(ref _pendingCount, pending);
        Interlocked.Exchange(ref _deadLetteredCount, deadLettered);
        Volatile.Write(ref _oldestPendingAgeSeconds, oldestPendingAgeSeconds);
    }
}

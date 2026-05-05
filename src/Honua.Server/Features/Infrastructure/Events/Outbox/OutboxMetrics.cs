// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Metrics emitted by the feature-change transactional outbox dispatcher.
/// Backlog/age gauges are observable so the dispatcher can update them under its
/// own cadence without competing with mutation hot paths.
/// </summary>
internal static class OutboxMetrics
{
    public static readonly Counter<long> Dispatched = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.outbox.dispatched_total",
        "events",
        "Feature-change outbox rows successfully dispatched to the canonical event publisher.");

    public static readonly Counter<long> Failed = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.outbox.failed_total",
        "events",
        "Feature-change outbox rows whose dispatch attempt failed (still retriable until dead-lettered).");

    public static readonly Counter<long> DeadLettered = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.outbox.dead_lettered_total",
        "events",
        "Feature-change outbox rows that exhausted retries and require operator triage.");

    public static readonly Counter<long> RecoveredClaims = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.outbox.recovered_claims_total",
        "claims",
        "Outbox claim leases reset by the recovery loop after a worker exit.");

    private static long _pendingCount;
    private static long _deadLetteredCount;
    private static double _oldestPendingAgeSeconds;

    static OutboxMetrics()
    {
        // Observable gauges report the dispatcher's most recent backlog snapshot.
        // The dispatcher refreshes the backing fields after each dispatch loop, so
        // these gauges read the cached values without re-querying the database on
        // every Prometheus scrape.
        HonuaTelemetry.Meter.CreateObservableGauge(
            "honua.outbox.pending_count",
            () => Interlocked.Read(ref _pendingCount),
            unit: "events",
            description: "Outbox rows waiting for dispatch (pending, claimed, or retriable).");

        HonuaTelemetry.Meter.CreateObservableGauge(
            "honua.outbox.dead_lettered_count",
            () => Interlocked.Read(ref _deadLetteredCount),
            unit: "events",
            description: "Outbox rows currently in the dead-letter state.");

        HonuaTelemetry.Meter.CreateObservableGauge(
            "honua.outbox.oldest_pending_age_seconds",
            () => Volatile.Read(ref _oldestPendingAgeSeconds),
            unit: "seconds",
            description: "Age in seconds of the oldest claimable outbox row.");
    }

    public static void RecordBacklog(long pending, long deadLettered, double oldestPendingAgeSeconds)
    {
        Interlocked.Exchange(ref _pendingCount, pending);
        Interlocked.Exchange(ref _deadLetteredCount, deadLettered);
        Volatile.Write(ref _oldestPendingAgeSeconds, oldestPendingAgeSeconds);
    }
}

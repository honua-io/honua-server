// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;

namespace Honua.Infrastructure.Events;

/// <summary>
/// Metrics emitted when committed feature-change events are fanned out to
/// broker-agnostic sinks (#357).
/// </summary>
internal static class FeatureChangeEventSinkMetrics
{
    private static readonly Meter Meter = HonuaTelemetry.Meter;

    // `unit: null` on the instruments below is deliberate, not an oversight. The OpenTelemetry
    // Prometheus exporter derives the exported series name from the instrument name AND its unit:
    // it maps the unit through the UCUM table and appends it, so a declared unit renames the series
    // out from under every dashboard and alert rule without breaking a single build. A PromQL query
    // against the absent name returns an empty vector, so the panel is blank and the alert never
    // fires, silently. Units are documented in the instrument name and description instead. See the
    // SLO-contract comment block in HonuaTelemetry.cs, observability/metric-name-contract.json, and
    // MetricNameContractTests, which scrapes the real /metrics exposition and fails on drift.

    /// <summary>
    /// Events successfully published to a sink, tagged by sink name.
    /// </summary>
    public static readonly Counter<long> Published = Meter.CreateCounter<long>(
        "honua.event_sink.published_total",
        unit: null,
        "Feature-change events successfully published to an event-bus sink.");

    /// <summary>
    /// Events whose sink publish attempt failed, tagged by sink name. A sink
    /// failure is isolated and does not affect durable storage, live streaming,
    /// or sibling sinks.
    /// </summary>
    public static readonly Counter<long> Failed = Meter.CreateCounter<long>(
        "honua.event_sink.failed_total",
        unit: null,
        "Feature-change events whose sink publish attempt failed.");

    /// <summary>
    /// Events routed to a sink's dead-letter destination after the primary
    /// delivery failed, tagged by sink name. A dead-lettered event is counted in
    /// addition to (not instead of) the primary <see cref="Failed"/> increment.
    /// </summary>
    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        "honua.event_sink.dead_lettered_total",
        unit: null,
        "Feature-change events routed to a sink dead-letter destination after a delivery failure.");
}

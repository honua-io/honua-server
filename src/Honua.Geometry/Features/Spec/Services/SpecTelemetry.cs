// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Honua.Core.Features.Spec.Services;

public static class SpecTelemetry
{
    // Shares the "Honua" meter/activity-source name with ServiceDefaults'
    // HonuaTelemetry so exporters pick these up alongside the server-wide
    // instruments. Honua.Core intentionally does not reference ServiceDefaults.
    private const string SourceName = "Honua";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    public const string PlanActivityName = "honua.spec.plan";

    public const string ApplyActivityName = "honua.spec.apply";

    public const string NodeActivityName = "honua.spec.node";

    public static readonly Counter<long> AppliesStarted =
        Meter.CreateCounter<long>("honua.spec.applies_started");

    public static readonly Counter<long> AppliesCompleted =
        Meter.CreateCounter<long>("honua.spec.applies_completed");

    public static readonly Counter<long> NodesProcessed =
        Meter.CreateCounter<long>("honua.spec.nodes_processed");

    public static readonly Histogram<double> ApplyDurationMs =
        // `unit: null` on the instruments below is deliberate, not an oversight. The OpenTelemetry
        // Prometheus exporter derives the exported series name from the instrument name AND its unit:
        // it maps the unit through the UCUM table and appends it, so a declared unit renames the series
        // out from under every dashboard and alert rule without breaking a single build. A PromQL query
        // against the absent name returns an empty vector, so the panel is blank and the alert never
        // fires, silently. Units are documented in the instrument name and description instead. See the
        // SLO-contract comment block in HonuaTelemetry.cs, eng/telemetry/metric-name-contract.v1.json, and
        // MetricNameContractTests, which scrapes the real /metrics exposition and fails on drift.

        Meter.CreateHistogram<double>(
            "honua.spec.apply_duration_ms",
            unit: null,
            description: "End-to-end apply duration including cache lookups.");

    public static readonly Histogram<double> NodeDurationMs =
        Meter.CreateHistogram<double>(
            "honua.spec.node_duration_ms",
            unit: null,
            description: "Per-node execution duration; excludes cache hits.");

    public static readonly Counter<long> CacheLookups =
        Meter.CreateCounter<long>("honua.spec.cache_lookups");

    public static void SetSuccess(Activity? activity)
    {
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public static void RecordException(Activity? activity, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, SanitizeTelemetryText(exception.Message));
        activity.SetTag("exception.type", exception.GetType().FullName);
        activity.SetTag("exception.message", SanitizeTelemetryText(exception.Message));
    }

    public static string SanitizeTelemetryText(string? value, int maxLength = 256)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

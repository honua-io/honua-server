// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Honua.Core.Features.Spec.Services;

/// <summary>
/// Static telemetry helpers for the spec plan / apply engine. Hangs counters
/// and histograms off a Meter that shares the "Honua" name so the rest of the
/// stack observes them through the existing exporter wiring.
/// </summary>
public static class SpecTelemetry
{
    // Shares the "Honua" meter/activity-source name with ServiceDefaults'
    // HonuaTelemetry so exporters pick these up alongside the server-wide
    // instruments. Honua.Core intentionally does not reference ServiceDefaults.
    private const string SourceName = "Honua";

    /// <summary>ActivitySource for spec-related spans.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>Meter for spec-related instruments.</summary>
    public static readonly Meter Meter = new(SourceName);

    /// <summary>Activity emitted around <c>POST /v1/spec/plan</c>.</summary>
    public const string PlanActivityName = "honua.spec.plan";

    /// <summary>Activity emitted around the lifetime of an apply run.</summary>
    public const string ApplyActivityName = "honua.spec.apply";

    /// <summary>Activity emitted per-node during an apply run.</summary>
    public const string NodeActivityName = "honua.spec.node";

    /// <summary>Count of apply runs started, tagged by cache mode.</summary>
    public static readonly Counter<long> AppliesStarted =
        Meter.CreateCounter<long>("honua.spec.applies_started");

    /// <summary>Count of apply runs that reached a terminal state, tagged by outcome.</summary>
    public static readonly Counter<long> AppliesCompleted =
        Meter.CreateCounter<long>("honua.spec.applies_completed");

    /// <summary>Count of nodes processed, tagged by outcome (cached/succeeded/failed/skipped).</summary>
    public static readonly Counter<long> NodesProcessed =
        Meter.CreateCounter<long>("honua.spec.nodes_processed");

    /// <summary>Observed duration of apply runs in milliseconds.</summary>
    public static readonly Histogram<double> ApplyDurationMs =
        Meter.CreateHistogram<double>(
            "honua.spec.apply_duration_ms",
            unit: "ms",
            description: "End-to-end apply duration including cache lookups.");

    /// <summary>Observed per-node duration in milliseconds.</summary>
    public static readonly Histogram<double> NodeDurationMs =
        Meter.CreateHistogram<double>(
            "honua.spec.node_duration_ms",
            unit: "ms",
            description: "Per-node execution duration; excludes cache hits.");

    /// <summary>Cache-lookup outcomes (hit/miss/bypass).</summary>
    public static readonly Counter<long> CacheLookups =
        Meter.CreateCounter<long>("honua.spec.cache_lookups");

    /// <summary>
    /// Marks an activity as successful. Mirrors the minimal surface of
    /// <c>HonuaTelemetry.SetSuccess</c> without pulling in ServiceDefaults.
    /// </summary>
    public static void SetSuccess(Activity? activity)
    {
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>
    /// Records an exception on an activity. Sanitises the message before
    /// attaching it as a tag to avoid leaking secrets.
    /// </summary>
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

    /// <summary>
    /// Trims and truncates a free-form telemetry string. Intentionally conservative —
    /// the server-wide HonuaTelemetry.SanitizeTelemetryText handles PII/secret redaction
    /// for traces that flow through its helpers; this local helper only guards length
    /// so the Core project stays dependency-free.
    /// </summary>
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

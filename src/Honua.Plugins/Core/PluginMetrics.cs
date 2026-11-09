// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Honua.Plugins;

internal static class PluginMetrics
{
    public const string PluginIdTag = "plugin_id";

    public const string ExtensionPointTag = "extension_point";

    private const string UnknownPluginId = "unknown";

    // Independent Meter instance sharing the canonical "Honua" meter name (see HonuaCacheMetrics)
    // so a single exporter selector collects every Honua counter without Honua.Plugins taking a
    // dependency on Honua.ServiceDefaults.
    private static readonly Meter Meter = new("Honua");

    // `unit: null` on the instruments below is deliberate, not an oversight. The OpenTelemetry
    // Prometheus exporter derives the exported series name from the instrument name AND its unit:
    // it maps the unit through the UCUM table and appends it, so a declared unit renames the series
    // out from under every dashboard and alert rule without breaking a single build. A PromQL query
    // against the absent name returns an empty vector, so the panel is blank and the alert never
    // fires, silently. Units are documented in the instrument name and description instead. See the
    // SLO-contract comment block in HonuaTelemetry.cs, eng/telemetry/metric-name-contract.v1.json, and
    // MetricNameContractTests, which scrapes the real /metrics exposition and fails on drift.

    private static readonly Counter<long> Invocations = Meter.CreateCounter<long>(
        "honua_plugin_invocations_total",
        unit: null,
        "Total plugin extension-point invocations, partitioned by plugin_id and extension_point.");

    private static readonly Counter<long> Failures = Meter.CreateCounter<long>(
        "honua_plugin_failures_total",
        unit: null,
        "Total plugin extension-point invocations that faulted, partitioned by plugin_id and extension_point.");

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "honua_plugin_duration_ms",
        unit: null,
        "Plugin extension-point execution duration in milliseconds, partitioned by plugin_id and extension_point.");

    internal static Meter InstrumentMeter => Meter;

    public static Scope Measure(string? pluginId, string extensionPoint)
    {
        var id = Normalize(pluginId);
        var tags = Tags(id, extensionPoint);
        Invocations.Add(1, tags[0], tags[1]);
        return new Scope(id, extensionPoint);
    }

    private static KeyValuePair<string, object?>[] Tags(string pluginId, string extensionPoint) =>
    [
        new(PluginIdTag, pluginId),
        new(ExtensionPointTag, extensionPoint),
    ];

    private static string Normalize(string? pluginId)
        => string.IsNullOrWhiteSpace(pluginId) ? UnknownPluginId : pluginId;

    public struct Scope : IDisposable
    {
        private readonly string _pluginId;
        private readonly string _extensionPoint;
        private readonly long _startTimestamp;
        private bool _failed;
        private bool _disposed;

        internal Scope(string pluginId, string extensionPoint)
        {
            _pluginId = pluginId;
            _extensionPoint = extensionPoint;
            _startTimestamp = Stopwatch.GetTimestamp();
            _failed = false;
            _disposed = false;
        }

        public void MarkFailed() => _failed = true;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var elapsedMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            var tags = Tags(_pluginId, _extensionPoint);
            Duration.Record(elapsedMs, tags[0], tags[1]);
            if (_failed)
            {
                Failures.Add(1, tags[0], tags[1]);
            }
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Honua.Plugins;

/// <summary>
/// OpenTelemetry instrumentation for plugin extension-point execution (issue #1562). Emits
/// invocation counters, a failure counter, and a duration histogram on the shared <c>Honua</c>
/// meter so the OpenTelemetry exporter wired in <c>HonuaTelemetry</c> picks them up alongside the
/// rest of the server's metrics. All series carry a low-cardinality <c>plugin_id</c> and
/// <c>extension_point</c> dimension so dashboards can compute per-plugin latency and error rates.
/// </summary>
/// <remarks>
/// Plugin ids and extension-point names are bounded (compile-time registered, one series per
/// plugin/extension), so the resulting metric cardinality stays small. Recording is allocation-light
/// on the hot path: callers wrap an execution in <see cref="Measure"/> and dispose the returned
/// scope, which records duration and a failure when <see cref="Scope.MarkFailed"/> was called.
/// </remarks>
internal static class PluginMetrics
{
    /// <summary>Tag key identifying the plugin that owns the executed extension point.</summary>
    public const string PluginIdTag = "plugin_id";

    /// <summary>Tag key identifying the extension-point family being executed.</summary>
    public const string ExtensionPointTag = "extension_point";

    private const string UnknownPluginId = "unknown";

    // Independent Meter instance sharing the canonical "Honua" meter name (see HonuaCacheMetrics)
    // so a single exporter selector collects every Honua counter without Honua.Plugins taking a
    // dependency on Honua.ServiceDefaults.
    private static readonly Meter Meter = new("Honua");

    private static readonly Counter<long> Invocations = Meter.CreateCounter<long>(
        "honua_plugin_invocations_total",
        "operations",
        "Total plugin extension-point invocations, partitioned by plugin_id and extension_point.");

    private static readonly Counter<long> Failures = Meter.CreateCounter<long>(
        "honua_plugin_failures_total",
        "operations",
        "Total plugin extension-point invocations that faulted, partitioned by plugin_id and extension_point.");

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "honua_plugin_duration_ms",
        "ms",
        "Plugin extension-point execution duration in milliseconds, partitioned by plugin_id and extension_point.");

    /// <summary>
    /// Begins timing a single plugin extension-point execution. Increments the invocation counter
    /// immediately and records duration (plus a failure when <see cref="Scope.MarkFailed"/> was
    /// called) when the returned scope is disposed.
    /// </summary>
    /// <param name="pluginId">The owning plugin id; falls back to <c>"unknown"</c> when null/empty.</param>
    /// <param name="extensionPoint">The extension-point family (e.g. <c>validate</c>, <c>compute</c>).</param>
    /// <returns>A disposable measurement scope.</returns>
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

    /// <summary>
    /// Disposable measurement of one plugin extension-point execution. Records elapsed time and, when
    /// <see cref="MarkFailed"/> was called, a failure on dispose.
    /// </summary>
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

        /// <summary>Flags this execution as failed so a failure counter is emitted on dispose.</summary>
        public void MarkFailed() => _failed = true;

        /// <summary>Records duration and (when flagged) a failure for this execution.</summary>
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

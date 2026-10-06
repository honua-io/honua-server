// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Xunit;

namespace Honua.Plugins.Tests;

public sealed class PluginMetricsTests : IClassFixture<PluginMetricsListenerFixture>
{
    private readonly PluginMetricsListenerFixture _metrics;

    public PluginMetricsTests(PluginMetricsListenerFixture metrics)
    {
        _metrics = metrics;
    }

    [Fact]
    public void Measure_EmitsInvocationAndDuration_WithPluginTags()
    {
        var pluginId = UniquePluginId();
        using var unrelatedMeter = new Meter("Honua");
        var unrelatedInvocations = unrelatedMeter.CreateCounter<long>("honua_plugin_invocations_total");
        var unrelatedDuration = unrelatedMeter.CreateHistogram<double>("honua_plugin_duration_ms");
        var unrelatedTags = new TagList
        {
            { PluginMetrics.PluginIdTag, pluginId },
            { PluginMetrics.ExtensionPointTag, "validate" },
        };
        unrelatedInvocations.Add(1, unrelatedTags);
        unrelatedDuration.Record(1, unrelatedTags);

        using (PluginMetrics.Measure(pluginId, "validate"))
        {
        }

        _metrics.Invocations.Should().ContainSingle(measurement =>
            measurement.Plugin == pluginId && measurement.Extension == "validate");
        _metrics.Durations.Should().ContainSingle(measurement =>
            measurement.Plugin == pluginId && measurement.Extension == "validate");
    }

    [Fact]
    public void Measure_EmitsFailure_WhenMarkedFailed()
    {
        var pluginId = UniquePluginId();
        using (var scope = PluginMetrics.Measure(pluginId, "compute"))
        {
            scope.MarkFailed();
        }

        _metrics.Failures.Should().ContainSingle(measurement =>
            measurement.Plugin == pluginId && measurement.Extension == "compute");
    }

    /// <summary>
    /// Regression test for #3734: the assertion above depends only on a per-invocation unique
    /// <c>plugin_id</c>, so it must hold even when many <see cref="PluginMetrics.Measure"/> calls
    /// race concurrently on the shared static <see cref="PluginMetrics.InstrumentMeter"/>
    /// instruments. The class fixture deliberately keeps one listener alive for the complete test
    /// class so unrelated listener disposal cannot interrupt these assertions.
    /// </summary>
    [Fact]
    public async Task Measure_EmitsInvocationAndDuration_WithPluginTags_UnderConcurrentLoad()
    {
        const int concurrency = 32;

        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(() =>
        {
            var pluginId = UniquePluginId();
            using (PluginMetrics.Measure(pluginId, "validate"))
            {
            }

            _metrics.Invocations.Should().ContainSingle(measurement =>
                measurement.Plugin == pluginId && measurement.Extension == "validate");
            _metrics.Durations.Should().ContainSingle(measurement =>
                measurement.Plugin == pluginId && measurement.Extension == "validate");
        })).ToArray();

        await Task.WhenAll(tasks);
    }

    private static string UniquePluginId() => $"p-{Guid.NewGuid():N}";
}

public sealed class PluginMetricsListenerFixture : IDisposable
{
    private readonly MeterListener _listener = new();

    public PluginMetricsListenerFixture()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, PluginMetrics.InstrumentMeter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            var measurement = ReadTags(tags);
            if (instrument.Name == "honua_plugin_invocations_total")
            {
                Invocations.Add(measurement);
            }
            else if (instrument.Name == "honua_plugin_failures_total")
            {
                Failures.Add(measurement);
            }
        });
        _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            if (instrument.Name == "honua_plugin_duration_ms")
            {
                Durations.Add(ReadTags(tags));
            }
        });
        _listener.Start();
    }

    public ConcurrentBag<(string Plugin, string Extension)> Invocations { get; } = [];

    public ConcurrentBag<(string Plugin, string Extension)> Failures { get; } = [];

    public ConcurrentBag<(string Plugin, string Extension)> Durations { get; } = [];

    public void Dispose() => _listener.Dispose();

    private static (string Plugin, string Extension) ReadTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string plugin = string.Empty;
        string extension = string.Empty;
        foreach (var tag in tags)
        {
            if (tag.Key == PluginMetrics.PluginIdTag)
            {
                plugin = tag.Value?.ToString() ?? string.Empty;
            }
            else if (tag.Key == PluginMetrics.ExtensionPointTag)
            {
                extension = tag.Value?.ToString() ?? string.Empty;
            }
        }

        return (plugin, extension);
    }
}

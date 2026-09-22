// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Xunit;

namespace Honua.Plugins.Tests;

public sealed class PluginMetricsTests
{
    [Fact]
    public void Measure_EmitsInvocationAndDuration_WithPluginTags()
    {
        var pluginId = UniquePluginId();
        var invocations = new List<(string Plugin, string Extension)>();
        var durations = new List<(string Plugin, string Extension)>();
        var meter = PluginMetrics.InstrumentMeter;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "honua_plugin_invocations_total")
            {
                invocations.Add(ReadTags(tags));
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "honua_plugin_duration_ms")
            {
                durations.Add(ReadTags(tags));
            }
        });
        listener.Start();

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

        listener.Dispose();

        invocations.Should().ContainSingle(measurement =>
            measurement.Plugin == pluginId && measurement.Extension == "validate");
        durations.Should().ContainSingle(measurement =>
            measurement.Plugin == pluginId && measurement.Extension == "validate");
    }

    [Fact]
    public void Measure_EmitsFailure_WhenMarkedFailed()
    {
        var pluginId = UniquePluginId();
        var failures = new List<(string Plugin, string Extension)>();
        var meter = PluginMetrics.InstrumentMeter;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "honua_plugin_failures_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => failures.Add(ReadTags(tags)));
        listener.Start();

        using (var scope = PluginMetrics.Measure(pluginId, "compute"))
        {
            scope.MarkFailed();
        }

        listener.Dispose();

        failures.Should().ContainSingle(measurement =>
            measurement.Plugin == pluginId && measurement.Extension == "compute");
    }

    /// <summary>
    /// Regression test for #3734: the assertion above depends only on a per-invocation unique
    /// <c>plugin_id</c>, so it must hold even when many <see cref="PluginMetrics.Measure"/> calls
    /// (and their own <see cref="MeterListener"/> instances) race concurrently on the shared static
    /// <see cref="PluginMetrics.InstrumentMeter"/> instruments, as happens under the full parallel
    /// test-matrix load that reopened this issue.
    /// </summary>
    [Fact]
    public async Task Measure_EmitsInvocationAndDuration_WithPluginTags_UnderConcurrentLoad()
    {
        const int concurrency = 32;

        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(() =>
        {
            var pluginId = UniquePluginId();
            var invocations = new ConcurrentBag<(string Plugin, string Extension)>();
            var durations = new ConcurrentBag<(string Plugin, string Extension)>();
            var meter = PluginMetrics.InstrumentMeter;

            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (instrument.Name == "honua_plugin_invocations_total")
                {
                    invocations.Add(ReadTags(tags));
                }
            });
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            {
                if (instrument.Name == "honua_plugin_duration_ms")
                {
                    durations.Add(ReadTags(tags));
                }
            });
            listener.Start();

            using (PluginMetrics.Measure(pluginId, "validate"))
            {
            }

            listener.Dispose();

            invocations.Should().ContainSingle(measurement =>
                measurement.Plugin == pluginId && measurement.Extension == "validate");
            durations.Should().ContainSingle(measurement =>
                measurement.Plugin == pluginId && measurement.Extension == "validate");
        })).ToArray();

        await Task.WhenAll(tasks);
    }

    private static string UniquePluginId() => $"p-{Guid.NewGuid():N}";

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

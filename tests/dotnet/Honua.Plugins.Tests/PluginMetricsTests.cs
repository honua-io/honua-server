// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using FluentAssertions;
using Xunit;

namespace Honua.Plugins.Tests;

public sealed class PluginMetricsTests
{
    [Fact]
    public void Measure_EmitsInvocationAndDuration_WithPluginTags()
    {
        var invocations = new List<(string Plugin, string Extension)>();
        var durations = new List<(string Plugin, string Extension)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Honua" && instrument.Name.StartsWith("honua_plugin_", StringComparison.Ordinal))
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

        using (PluginMetrics.Measure("p1", "validate"))
        {
        }

        listener.Dispose();

        invocations.Should().ContainSingle()
            .Which.Should().Be(("p1", "validate"));
        durations.Should().ContainSingle()
            .Which.Should().Be(("p1", "validate"));
    }

    [Fact]
    public void Measure_EmitsFailure_WhenMarkedFailed()
    {
        var failures = new List<(string Plugin, string Extension)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "honua_plugin_failures_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => failures.Add(ReadTags(tags)));
        listener.Start();

        using (var scope = PluginMetrics.Measure("p2", "compute"))
        {
            scope.MarkFailed();
        }

        listener.Dispose();

        failures.Should().ContainSingle().Which.Should().Be(("p2", "compute"));
    }

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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Verifies that the Lambda cold-start counter and init-duration histogram are
/// emitted through the shared <see cref="LambdaTelemetry.MeterName"/> meter so
/// they reach the existing Prometheus/OTLP exporters.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
[Collection("HonuaTelemetry")]
public sealed class LambdaTelemetryTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void EmitColdStart_RecordsCounterAndHistogram_WithLambdaTags()
    {
        var context = new LambdaContext(
            functionName: "honua-demo",
            functionVersion: "7",
            initializationType: "on-demand",
            memoryLimitMib: 2048);

        var coldStarts = new List<MeasurementSample<long>>();
        var initDurations = new List<MeasurementSample<double>>();

        using (var listener = new MeterListener())
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == LambdaTelemetry.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };

            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (instrument.Name == "honua.lambda.cold_start")
                {
                    coldStarts.Add(new MeasurementSample<long>(value, TagsToDictionary(tags)));
                }
            });

            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            {
                if (instrument.Name == "honua.lambda.init_duration_ms")
                {
                    initDurations.Add(new MeasurementSample<double>(value, TagsToDictionary(tags)));
                }
            });

            listener.Start();

            LambdaTelemetry.EmitColdStart(context, initDurationMs: 1234.5);
        }

        var coldStart = Assert.Single(coldStarts);
        Assert.Equal(1, coldStart.Value);
        Assert.Equal("honua-demo", coldStart.Tags["function.name"]);
        Assert.Equal("7", coldStart.Tags["function.version"]);
        Assert.Equal("on-demand", coldStart.Tags["init.type"]);
        Assert.Equal(2048, coldStart.Tags["memory.limit_mib"]);

        var initDuration = Assert.Single(initDurations);
        Assert.Equal(1234.5, initDuration.Value);
        Assert.Equal("honua-demo", initDuration.Tags["function.name"]);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void EmitColdStart_NegativeDuration_SkipsHistogramSample()
    {
        var context = new LambdaContext("honua-demo", "1", "on-demand", 512);

        var initDurations = new List<double>();

        using (var listener = new MeterListener())
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == LambdaTelemetry.MeterName
                    && instrument.Name == "honua.lambda.init_duration_ms")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };

            listener.SetMeasurementEventCallback<double>((_, value, _, _) => initDurations.Add(value));
            listener.Start();

            LambdaTelemetry.EmitColdStart(context, initDurationMs: -1);
        }

        Assert.Empty(initDurations);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void RecordColdStart_OffLambda_IsNoOp()
    {
        // The host test runner is not a Lambda execution environment, so the
        // production entry point must not emit anything.
        Assert.False(LambdaTelemetry.Context.IsLambda);
        Assert.False(LambdaTelemetry.RecordColdStart());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void BuildTags_DefaultsUnknownValues_WhenContextEmpty()
    {
        var context = new LambdaContext(null, null, null, 0);

        var tags = context.BuildTags().ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal("unknown", tags["function.name"]);
        Assert.Equal("unknown", tags["function.version"]);
        Assert.Equal("on-demand", tags["init.type"]);
        Assert.Equal(0, tags["memory.limit_mib"]);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void XRayTracingOptions_DefaultsDisabled()
    {
        var options = new TracingOptions();

        Assert.NotNull(options.XRay);
        Assert.False(options.XRay.Enabled);
    }

    private static Dictionary<string, object?> TagsToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dictionary = new Dictionary<string, object?>(tags.Length);
        foreach (var tag in tags)
        {
            dictionary[tag.Key] = tag.Value;
        }

        return dictionary;
    }

    private readonly record struct MeasurementSample<T>(T Value, Dictionary<string, object?> Tags)
        where T : struct;
}

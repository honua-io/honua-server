// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using Xunit;

namespace Honua.ServiceDefaults.UnitTests.Source.Features.Infrastructure.Monitoring;

public sealed class TracingSamplerTests
{
    [Fact]
    [Trait("Tier", "Fast")]
    public void CreateSampler_WhenAdaptiveEnabled_WrapsInParentBasedSampler()
    {
        using var metricsCollector = new SystemMetricsCollector();
        var adaptiveOptions = new AdaptiveSamplingOptions
        {
            Enabled = true,
            BaseSamplingRate = 0.0,
            MinSamplingRate = 0.0,
            MaxSamplingRate = 0.0
        };
        using var adaptiveSampler = new AdaptiveSampler(
            Options.Create(adaptiveOptions),
            metricsCollector,
            NullLogger<AdaptiveSampler>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(adaptiveOptions));
        services.AddSingleton(adaptiveSampler);
        using var provider = services.BuildServiceProvider();

        var sampler = Extensions.CreateSampler(provider, new TracingOptions { Enabled = true, SamplingRatio = 0.1 });

        sampler.Should().BeOfType<ParentBasedSampler>();
        AssertParentDecisionRespected(sampler);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void CreateSampler_WhenRatioBetweenZeroAndOne_WrapsInParentBasedSampler()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var sampler = Extensions.CreateSampler(provider, new TracingOptions { Enabled = true, SamplingRatio = 0.1 });

        sampler.Should().BeOfType<ParentBasedSampler>();
        AssertParentDecisionRespected(sampler);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void CreateSampler_WhenRatioAtEdges_UsesAlwaysOffOrAlwaysOn()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        Extensions.CreateSampler(provider, new TracingOptions { Enabled = true, SamplingRatio = 0.0 })
            .Should().BeOfType<AlwaysOffSampler>();
        Extensions.CreateSampler(provider, new TracingOptions { Enabled = true, SamplingRatio = 1.0 })
            .Should().BeOfType<AlwaysOnSampler>();
    }

    private static void AssertParentDecisionRespected(Sampler sampler)
    {
        var traceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();

        var sampledParent = new ActivityContext(traceId, parentSpanId, ActivityTraceFlags.Recorded, isRemote: true);
        var sampledParameters = new SamplingParameters(sampledParent, traceId, "child.op", ActivityKind.Server);
        sampler.ShouldSample(sampledParameters).Decision.Should().Be(SamplingDecision.RecordAndSample);

        var unsampledParent = new ActivityContext(traceId, parentSpanId, ActivityTraceFlags.None, isRemote: true);
        var unsampledParameters = new SamplingParameters(unsampledParent, traceId, "child.op", ActivityKind.Server);
        sampler.ShouldSample(unsampledParameters).Decision.Should().Be(SamplingDecision.Drop);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Deployment.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Deployment.Domain;

public class RuntimeStateTests
{
    [UnitTest]
    public void Unknown_ShouldReturnStateWithUnknownHealthAndNoObservation()
    {
        var state = RuntimeState.Unknown();

        state.Health.Should().Be(RuntimeHealth.Unknown);
        state.PublicUrl.Should().BeNull();
        state.LastObservedAt.Should().BeNull();
        state.LastHealthCheckAt.Should().BeNull();
        state.Message.Should().BeNull();
        state.Warnings.Should().BeEmpty();
    }

    [UnitTest]
    public void Healthy_ShouldSetHealthAndStampObservationTimes()
    {
        var observedAt = DateTimeOffset.UtcNow;

        var state = RuntimeState.Healthy(publicUrl: "/apps/flood", observedAt: observedAt);

        state.Health.Should().Be(RuntimeHealth.Healthy);
        state.PublicUrl.Should().Be("/apps/flood");
        state.LastObservedAt.Should().Be(observedAt);
        state.LastHealthCheckAt.Should().Be(observedAt);
        state.Message.Should().BeNull();
    }

    [UnitTest]
    public void Healthy_WhenObservedAtNull_ShouldUseCurrentTime()
    {
        var state = RuntimeState.Healthy();

        state.LastObservedAt.Should().NotBeNull();
        state.LastObservedAt!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [UnitTest]
    public void Degraded_ShouldCaptureHealthAndMessage()
    {
        var observedAt = DateTimeOffset.UtcNow;

        var state = RuntimeState.Degraded("Latency elevated", publicUrl: "/apps/flood", observedAt: observedAt);

        state.Health.Should().Be(RuntimeHealth.Degraded);
        state.Message.Should().Be("Latency elevated");
        state.PublicUrl.Should().Be("/apps/flood");
        state.LastObservedAt.Should().Be(observedAt);
        state.LastHealthCheckAt.Should().Be(observedAt);
    }

    [UnitTest]
    public void Unhealthy_ShouldCaptureHealthAndMessage()
    {
        var observedAt = DateTimeOffset.UtcNow;

        var state = RuntimeState.Unhealthy("Upstream unavailable", publicUrl: "/apps/flood", observedAt: observedAt);

        state.Health.Should().Be(RuntimeHealth.Unhealthy);
        state.Message.Should().Be("Upstream unavailable");
        state.PublicUrl.Should().Be("/apps/flood");
        state.LastObservedAt.Should().Be(observedAt);
        state.LastHealthCheckAt.Should().Be(observedAt);
    }

    [UnitTest]
    public void Degraded_WithNoPublicUrl_ShouldAllowNull()
    {
        var state = RuntimeState.Degraded("Missing readiness probe");

        state.PublicUrl.Should().BeNull();
        state.Message.Should().Be("Missing readiness probe");
    }

    [UnitTest]
    public void Warnings_CanBePopulatedViaInitOnly()
    {
        var state = RuntimeState.Healthy() with { Warnings = ["warmup pending", "cache miss"] };

        state.Warnings.Should().Equal("warmup pending", "cache miss");
    }
}

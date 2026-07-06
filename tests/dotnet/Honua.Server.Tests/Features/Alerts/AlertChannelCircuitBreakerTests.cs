// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AlertChannelCircuitBreakerTests
{
    private static AlertChannelCircuitBreaker Create(int threshold = 3, TimeSpan? cooldown = null)
        => new(Options.Create(new AlertOptions
        {
            Dispatch = new AlertDispatchOptions
            {
                CircuitBreakerThreshold = threshold,
                CircuitBreakerCooldown = cooldown ?? TimeSpan.FromMinutes(5),
            },
        }));

    [UnitTest]
    public void ShouldAttemptDelivery_WhenClosed_ReturnsTrue()
    {
        var breaker = Create();
        breaker.ShouldAttemptDelivery(AlertChannelType.Webhook, DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [UnitTest]
    public void RecordDeadLetter_TripsOpenAtThreshold_AndDefersDelivery()
    {
        var now = DateTimeOffset.UtcNow;
        var breaker = Create(threshold: 3, cooldown: TimeSpan.FromMinutes(10));

        breaker.RecordDeadLetter(AlertChannelType.Webhook, now).Should().BeFalse("below threshold");
        breaker.RecordDeadLetter(AlertChannelType.Webhook, now).Should().BeFalse("below threshold");
        breaker.RecordDeadLetter(AlertChannelType.Webhook, now).Should().BeTrue("threshold reached — breaker opens");

        breaker.IsOpen(AlertChannelType.Webhook, now).Should().BeTrue();
        breaker.ShouldAttemptDelivery(AlertChannelType.Webhook, now).Should().BeFalse("open channel defers delivery");
        breaker.NextProbeAt(AlertChannelType.Webhook, now).Should().Be(now.AddMinutes(10));
    }

    [UnitTest]
    public void ShouldAttemptDelivery_AfterCooldown_AdmitsSingleHalfOpenProbe()
    {
        var now = DateTimeOffset.UtcNow;
        var breaker = Create(threshold: 1, cooldown: TimeSpan.FromMinutes(5));
        breaker.RecordDeadLetter(AlertChannelType.Webhook, now);

        var afterCooldown = now.AddMinutes(5);
        breaker.ShouldAttemptDelivery(AlertChannelType.Webhook, afterCooldown).Should().BeTrue("cooldown elapsed — one probe admitted");
        breaker.ShouldAttemptDelivery(AlertChannelType.Webhook, afterCooldown).Should().BeFalse("the probe window was pushed out; no second concurrent probe");
    }

    [UnitTest]
    public void RecordSuccess_ClosesBreaker()
    {
        var now = DateTimeOffset.UtcNow;
        var breaker = Create(threshold: 1);
        breaker.RecordDeadLetter(AlertChannelType.Webhook, now);
        breaker.IsOpen(AlertChannelType.Webhook, now).Should().BeTrue();

        breaker.RecordSuccess(AlertChannelType.Webhook);

        breaker.IsOpen(AlertChannelType.Webhook, now).Should().BeFalse();
        breaker.ShouldAttemptDelivery(AlertChannelType.Webhook, now).Should().BeTrue();
    }

    [UnitTest]
    public void CircuitBreaking_WhenDisabled_NeverTrips()
    {
        var now = DateTimeOffset.UtcNow;
        var breaker = Create(threshold: 0);

        for (var i = 0; i < 10; i++)
        {
            breaker.RecordDeadLetter(AlertChannelType.Webhook, now).Should().BeFalse();
        }

        breaker.IsOpen(AlertChannelType.Webhook, now).Should().BeFalse();
        breaker.ShouldAttemptDelivery(AlertChannelType.Webhook, now).Should().BeTrue();
    }
}

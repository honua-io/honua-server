// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AlertNotificationRateLimiterTests
{
    [UnitTest]
    public void TryAcquire_UnderCap_AdmitsUpToLimit()
    {
        var limiter = new AlertNotificationRateLimiter();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 3; i++)
        {
            limiter.TryAcquire(AlertChannelType.Webhook, maxPerMinute: 3, now).Should().BeTrue();
        }

        limiter.TryAcquire(AlertChannelType.Webhook, maxPerMinute: 3, now).Should().BeFalse(
            "the fourth acquisition within the window exceeds the cap of 3");
    }

    [UnitTest]
    public void TryAcquire_ZeroCap_AlwaysAdmits()
    {
        var limiter = new AlertNotificationRateLimiter();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 1000; i++)
        {
            limiter.TryAcquire(AlertChannelType.Slack, maxPerMinute: 0, now).Should().BeTrue(
                "a cap of 0 disables rate limiting");
        }
    }

    [UnitTest]
    public void TryAcquire_IsPerChannel()
    {
        var limiter = new AlertNotificationRateLimiter();
        var now = DateTimeOffset.UtcNow;

        limiter.TryAcquire(AlertChannelType.Webhook, maxPerMinute: 1, now).Should().BeTrue();
        limiter.TryAcquire(AlertChannelType.Webhook, maxPerMinute: 1, now).Should().BeFalse();

        // A different channel has its own independent window.
        limiter.TryAcquire(AlertChannelType.Slack, maxPerMinute: 1, now).Should().BeTrue();
    }

    [UnitTest]
    public void TryAcquire_WindowSlides_FreesCapacityAfterOneMinute()
    {
        var limiter = new AlertNotificationRateLimiter();
        var start = DateTimeOffset.UtcNow;

        limiter.TryAcquire(AlertChannelType.Webhook, maxPerMinute: 1, start).Should().BeTrue();
        limiter.TryAcquire(AlertChannelType.Webhook, maxPerMinute: 1, start).Should().BeFalse();

        var afterWindow = start.AddSeconds(61);
        limiter.TryAcquire(AlertChannelType.Webhook, maxPerMinute: 1, afterWindow).Should().BeTrue(
            "the earlier admission aged out of the rolling minute window");
    }
}

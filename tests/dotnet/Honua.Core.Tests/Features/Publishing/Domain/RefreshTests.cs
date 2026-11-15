// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Publishing.Domain;

public class RefreshPolicyTests
{
    [UnitTest]
    public void Manual_ShouldCreateManualPolicy()
    {
        var policy = RefreshPolicy.Manual();

        policy.Mode.Should().Be(RefreshMode.Manual);
        policy.Interval.Should().BeNull();
        policy.LastRefreshAt.Should().BeNull();
        policy.NextRefreshAt.Should().BeNull();
    }

    [UnitTest]
    public void Scheduled_ShouldCreatePolicyWithIntervalAndNextRefresh()
    {
        var interval = TimeSpan.FromHours(4);

        var policy = RefreshPolicy.Scheduled(interval);

        policy.Mode.Should().Be(RefreshMode.Scheduled);
        policy.Interval.Should().Be(interval);
        policy.LastRefreshAt.Should().BeNull();
        policy.NextRefreshAt.Should().NotBeNull();
        policy.NextRefreshAt.Should().BeCloseTo(DateTimeOffset.UtcNow + interval, TimeSpan.FromSeconds(2));
    }

    [UnitTest]
    public void Scheduled_WithZeroInterval_ShouldThrow()
    {
        var act = () => RefreshPolicy.Scheduled(TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("interval");
    }

    [UnitTest]
    public void Scheduled_WithNegativeInterval_ShouldThrow()
    {
        var act = () => RefreshPolicy.Scheduled(TimeSpan.FromSeconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("interval");
    }

    [UnitTest]
    public void WithRefreshCompleted_Manual_ShouldUpdateLastRefreshWithoutNext()
    {
        var policy = RefreshPolicy.Manual();

        var updated = policy.WithRefreshCompleted();

        updated.LastRefreshAt.Should().NotBeNull();
        updated.LastRefreshAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
        updated.NextRefreshAt.Should().BeNull();
    }

    [UnitTest]
    public void WithRefreshCompleted_Scheduled_ShouldAdvanceNextRefreshTime()
    {
        var interval = TimeSpan.FromHours(2);
        var policy = RefreshPolicy.Scheduled(interval);

        var updated = policy.WithRefreshCompleted();

        updated.LastRefreshAt.Should().NotBeNull();
        updated.NextRefreshAt.Should().NotBeNull();
        updated.NextRefreshAt.Should().BeCloseTo(DateTimeOffset.UtcNow + interval, TimeSpan.FromSeconds(2));
    }

    [UnitTest]
    public void WithRefreshCompleted_ShouldPreserveMode()
    {
        var scheduled = RefreshPolicy.Scheduled(TimeSpan.FromMinutes(30));
        var manual = RefreshPolicy.Manual();

        scheduled.WithRefreshCompleted().Mode.Should().Be(RefreshMode.Scheduled);
        manual.WithRefreshCompleted().Mode.Should().Be(RefreshMode.Manual);
    }
}

public class RefreshRequestTests
{

}

public class RefreshResultTests
{
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Deployment.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Deployment.Domain;

public class DeploymentScheduleTests
{
    [UnitTest]
    public void Immediate_ShouldLeavePublishWindowOpen()
    {
        var schedule = DeploymentSchedule.Immediate();

        schedule.PublishAt.Should().BeNull();
        schedule.UnpublishAt.Should().BeNull();
    }

    [UnitTest]
    public void At_ShouldSetPublishAt()
    {
        var at = DateTimeOffset.UtcNow.AddHours(3);

        var schedule = DeploymentSchedule.At(at);

        schedule.PublishAt.Should().Be(at);
        schedule.UnpublishAt.Should().BeNull();
    }

    [UnitTest]
    public void Window_WithValidInterval_ShouldSetBothBounds()
    {
        var start = DateTimeOffset.UtcNow.AddHours(1);
        var end = start.AddHours(2);

        var schedule = DeploymentSchedule.Window(start, end);

        schedule.PublishAt.Should().Be(start);
        schedule.UnpublishAt.Should().Be(end);
    }

    [UnitTest]
    public void Window_WhenEndEqualsStart_ShouldThrow()
    {
        var start = DateTimeOffset.UtcNow;

        var act = () => DeploymentSchedule.Window(start, start);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("unpublishAt");
    }

    [UnitTest]
    public void Window_WhenEndBeforeStart_ShouldThrow()
    {
        var start = DateTimeOffset.UtcNow.AddHours(2);
        var end = DateTimeOffset.UtcNow;

        var act = () => DeploymentSchedule.Window(start, end);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("unpublishAt");
    }

    [UnitTest]
    public void IsDue_WhenPublishAtNull_ShouldAlwaysBeTrue()
    {
        var schedule = DeploymentSchedule.Immediate();

        schedule.IsDue(DateTimeOffset.UtcNow).Should().BeTrue();
        schedule.IsDue(DateTimeOffset.MinValue).Should().BeTrue();
    }

    [UnitTest]
    public void IsDue_BeforePublishTime_ShouldBeFalse()
    {
        var publishAt = DateTimeOffset.UtcNow.AddHours(1);
        var schedule = DeploymentSchedule.At(publishAt);

        schedule.IsDue(publishAt.AddMinutes(-1)).Should().BeFalse();
    }

    [UnitTest]
    public void IsDue_AtPublishTime_ShouldBeTrue()
    {
        var publishAt = DateTimeOffset.UtcNow;
        var schedule = DeploymentSchedule.At(publishAt);

        schedule.IsDue(publishAt).Should().BeTrue();
    }

    [UnitTest]
    public void IsDue_AfterPublishTime_ShouldBeTrue()
    {
        var publishAt = DateTimeOffset.UtcNow.AddHours(-1);
        var schedule = DeploymentSchedule.At(publishAt);

        schedule.IsDue(DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [UnitTest]
    public void IsExpired_WhenUnpublishAtNull_ShouldAlwaysBeFalse()
    {
        var schedule = DeploymentSchedule.At(DateTimeOffset.UtcNow);

        schedule.IsExpired(DateTimeOffset.UtcNow.AddYears(10)).Should().BeFalse();
    }

    [UnitTest]
    public void IsExpired_BeforeUnpublishTime_ShouldBeFalse()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddHours(2);
        var schedule = DeploymentSchedule.Window(start, end);

        schedule.IsExpired(end.AddMinutes(-1)).Should().BeFalse();
    }

    [UnitTest]
    public void IsExpired_AtUnpublishTime_ShouldBeTrue()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddHours(2);
        var schedule = DeploymentSchedule.Window(start, end);

        schedule.IsExpired(end).Should().BeTrue();
    }

    [UnitTest]
    public void IsExpired_AfterUnpublishTime_ShouldBeTrue()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddHours(2);
        var schedule = DeploymentSchedule.Window(start, end);

        schedule.IsExpired(end.AddMinutes(5)).Should().BeTrue();
    }
}

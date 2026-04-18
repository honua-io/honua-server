// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Deployment.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Deployment.Domain;

public class RolloutPlanTests
{
    [UnitTest]
    public void Immediate_ShouldReturnAutoPromotingSingleStepPlan()
    {
        var plan = RolloutPlan.Immediate();

        plan.Strategy.Should().Be(RolloutStrategy.Immediate);
        plan.Steps.Should().BeEmpty();
        plan.AutoPromote.Should().BeTrue();
    }

    [UnitTest]
    public void BlueGreen_Default_ShouldAutoPromote()
    {
        var plan = RolloutPlan.BlueGreen();

        plan.Strategy.Should().Be(RolloutStrategy.BlueGreen);
        plan.Steps.Should().BeEmpty();
        plan.AutoPromote.Should().BeTrue();
    }

    [UnitTest]
    public void BlueGreen_WithManualPromotion_ShouldSetAutoPromoteFalse()
    {
        var plan = RolloutPlan.BlueGreen(autoPromote: false);

        plan.AutoPromote.Should().BeFalse();
    }

    [UnitTest]
    public void Canary_WithValidSteps_ShouldStoreSteps()
    {
        var plan = RolloutPlan.Canary([10, 50, 100]);

        plan.Strategy.Should().Be(RolloutStrategy.Canary);
        plan.Steps.Should().Equal(10, 50, 100);
        plan.AutoPromote.Should().BeTrue();
    }

    [UnitTest]
    public void Canary_WithManualPromotion_ShouldSetAutoPromoteFalse()
    {
        var plan = RolloutPlan.Canary([50, 100], autoPromote: false);

        plan.AutoPromote.Should().BeFalse();
    }

    [UnitTest]
    public void Canary_WithEmptySteps_ShouldThrow()
    {
        var act = () => RolloutPlan.Canary([]);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("steps")
            .WithMessage("*at least one step*");
    }

    [UnitTest]
    public void Canary_WithStepBelowRange_ShouldThrow()
    {
        var act = () => RolloutPlan.Canary([0, 100]);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("steps")
            .WithMessage("*outside the inclusive range*");
    }

    [UnitTest]
    public void Canary_WithStepAboveRange_ShouldThrow()
    {
        var act = () => RolloutPlan.Canary([50, 101]);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("steps");
    }

    [UnitTest]
    public void Canary_WithNonIncreasingSteps_ShouldThrow()
    {
        var act = () => RolloutPlan.Canary([50, 50, 100]);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("steps")
            .WithMessage("*strictly increase*");
    }

    [UnitTest]
    public void Canary_WithDecreasingSteps_ShouldThrow()
    {
        var act = () => RolloutPlan.Canary([80, 50, 100]);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("steps");
    }

    [UnitTest]
    public void Canary_WhenFinalStepNotHundred_ShouldThrow()
    {
        var act = () => RolloutPlan.Canary([10, 50, 90]);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("steps")
            .WithMessage("*Final canary step must be 100*");
    }

    [UnitTest]
    public void Canary_WithNullSteps_ShouldThrow()
    {
        var act = () => RolloutPlan.Canary(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [UnitTest]
    public void Scheduled_ShouldRequireManualPromotion()
    {
        var plan = RolloutPlan.Scheduled();

        plan.Strategy.Should().Be(RolloutStrategy.Scheduled);
        plan.Steps.Should().BeEmpty();
        plan.AutoPromote.Should().BeFalse();
    }
}

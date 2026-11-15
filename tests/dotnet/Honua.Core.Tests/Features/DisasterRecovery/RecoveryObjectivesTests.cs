// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.DisasterRecovery.Domain;

namespace Honua.Core.Tests.Features.DisasterRecovery;

/// <summary>
/// Unit tests for the recovery objectives and backup schedule value objects (#356).
/// </summary>
public sealed class RecoveryObjectivesTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public void Constructor_ValidObjectives_AreStored()
    {
        var objectives = new RecoveryObjectives(TimeSpan.FromHours(2), TimeSpan.FromMinutes(10));

        objectives.RecoveryTimeObjective.Should().Be(TimeSpan.FromHours(2));
        objectives.RecoveryPointObjective.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Default_HasOneHourRtoAndFiveMinuteRpo()
    {
        RecoveryObjectives.Default.RecoveryTimeObjective.Should().Be(TimeSpan.FromHours(1));
        RecoveryObjectives.Default.RecoveryPointObjective.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveRto_Throws(int seconds)
    {
        var act = () => new RecoveryObjectives(TimeSpan.FromSeconds(seconds), TimeSpan.FromMinutes(5));

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("recoveryTimeObjective");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveRpo_Throws(int seconds)
    {
        var act = () => new RecoveryObjectives(TimeSpan.FromHours(1), TimeSpan.FromSeconds(seconds));

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("recoveryPointObjective");
    }
}

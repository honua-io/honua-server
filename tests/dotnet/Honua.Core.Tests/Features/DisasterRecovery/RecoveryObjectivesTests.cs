// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

    [Trait("Tier", "Fast")]
    [Fact]
    public void BackupSchedule_WalIntervalWithinRpo_SatisfiesObjective()
    {
        var schedule = new BackupSchedule(TimeSpan.FromDays(1), TimeSpan.FromMinutes(5));

        schedule.SatisfiesRecoveryPointObjective(RecoveryObjectives.Default).Should().BeTrue();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void BackupSchedule_WalIntervalExceedsRpo_DoesNotSatisfyObjective()
    {
        var schedule = new BackupSchedule(TimeSpan.FromDays(1), TimeSpan.FromMinutes(15));

        schedule.SatisfiesRecoveryPointObjective(RecoveryObjectives.Default).Should().BeFalse();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void BackupSchedule_Default_SatisfiesDefaultObjective()
    {
        BackupSchedule.Default.SatisfiesRecoveryPointObjective(RecoveryObjectives.Default)
            .Should().BeTrue();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BackupSchedule_NonPositiveBaseInterval_Throws(int seconds)
    {
        var act = () => new BackupSchedule(TimeSpan.FromSeconds(seconds), TimeSpan.FromMinutes(5));

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("baseBackupInterval");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void BackupRecord_DataProtectingKinds_AreBaseAndWal()
    {
        var now = DateTimeOffset.UtcNow;
        new BackupRecord("b", BackupKind.PostgresBase, now, true).IsDataProtecting.Should().BeTrue();
        new BackupRecord("w", BackupKind.PostgresWal, now, true).IsDataProtecting.Should().BeTrue();
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;

namespace Honua.Core.Tests.Features.Assets;

[Trait("Tier", "Fast")]
public sealed class ProgrammeCalendarTests
{
    [Fact]
    public void MonthlyCycles_ClampEachCycleAgainstOriginalAnchor()
    {
        var calendar = Calendar(new DateTime(2028, 1, 31, 9, 0, 0), ProgrammeIntervalUnit.Month);

        Assert.Equal(Utc(2028, 2, 29), calendar.GetDueAt(1));
        Assert.Equal(Utc(2028, 3, 31), calendar.GetDueAt(2));
        Assert.Equal(Utc(2029, 2, 28), calendar.GetDueAt(13));
    }

    [Fact]
    public void YearlyCycles_KeepLeapDayAnchor()
    {
        var calendar = Calendar(new DateTime(2028, 2, 29, 9, 0, 0), ProgrammeIntervalUnit.Year);

        Assert.Equal(Utc(2029, 2, 28), calendar.GetDueAt(1));
        Assert.Equal(Utc(2032, 2, 29), calendar.GetDueAt(4));
    }

    [Theory]
    [InlineData(ProgrammeIntervalUnit.Day, 3, 7)]
    [InlineData(ProgrammeIntervalUnit.Week, 2, 29)]
    public void IntervalMultiplier_IsAppliedToCycleOrdinal(
        ProgrammeIntervalUnit unit, int interval, int expectedDay)
    {
        var calendar = new ProgrammeCalendar(new DateTime(2026, 1, 1, 9, 0, 0), new(unit, interval), "Etc/UTC");

        Assert.Equal(Utc(2026, 1, expectedDay), calendar.GetDueAt(2));
    }

    [Fact]
    public void DailyCycles_KeepLocalWallTimeAcrossDaylightSaving()
    {
        var calendar = Calendar(new DateTime(2026, 3, 7, 9, 0, 0), zone: "America/New_York");

        Assert.Equal(new DateTimeOffset(2026, 3, 7, 14, 0, 0, TimeSpan.Zero), calendar.GetDueAt(0));
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 13, 0, 0, TimeSpan.Zero), calendar.GetDueAt(1));
    }

    [Fact]
    public void MissingLocalTime_MovesToFirstValidMinuteWithoutMovingFutureAnchor()
    {
        var calendar = Calendar(new DateTime(2026, 3, 7, 2, 30, 15), zone: "America/New_York");

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), calendar.GetDueAt(1));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 6, 30, 15, TimeSpan.Zero), calendar.GetDueAt(2));
    }

    [Fact]
    public void RepeatedLocalTime_UsesEarlierOccurrence()
    {
        var calendar = Calendar(new DateTime(2026, 10, 31, 1, 30, 0), zone: "America/New_York");

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), calendar.GetDueAt(1));
    }

    [Fact]
    public void HalfHourDaylightSavingGap_ResolvesToFirstValidMinute()
    {
        var calendar = Calendar(new DateTime(2026, 10, 3, 2, 15, 0), zone: "Australia/Lord_Howe");

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 15, 30, 0, TimeSpan.Zero), calendar.GetDueAt(1));
    }

    [Fact]
    public void SkippedLocalDate_ResolvesWithoutMovingSubsequentCycle()
    {
        var calendar = Calendar(new DateTime(2011, 12, 29, 9, 0, 0), zone: "Pacific/Apia");

        Assert.Equal(new DateTimeOffset(2011, 12, 30, 10, 0, 0, TimeSpan.Zero), calendar.GetDueAt(1));
        Assert.Equal(new DateTimeOffset(2011, 12, 30, 19, 0, 0, TimeSpan.Zero), calendar.GetDueAt(2));
    }

    [Fact]
    public void CompletionBasedDueDate_ResolvesDaylightSavingGap()
    {
        var calendar = Calendar(new DateTime(2026, 1, 1), zone: "America/New_York");
        var acceptedCompletion = new DateTimeOffset(2026, 3, 7, 7, 30, 15, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), calendar.GetDueAfterCompletion(acceptedCompletion));
    }

    [Fact]
    public void CompletionBasedDueDate_UsesAcceptedCompletionInProgrammeTimezone()
    {
        var calendar = Calendar(new DateTime(2026, 1, 1, 9, 0, 0), ProgrammeIntervalUnit.Month,
            zone: "Pacific/Honolulu");
        var acceptedCompletion = new DateTimeOffset(2026, 2, 1, 8, 45, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 3, 1, 8, 45, 0, TimeSpan.Zero),
            calendar.GetDueAfterCompletion(acceptedCompletion));
    }

    [Fact]
    public void Preview_ResumesEveryMissedCycleWithoutSkippingOrCollapsing()
    {
        var calendar = Calendar(new DateTime(2026, 1, 1, 9, 0, 0));
        var first = calendar.Preview(0, 2, new(Utc(2026, 1, 5)));
        var second = calendar.Preview(first.NextCycleIndex!.Value, 2, new(Utc(2026, 1, 5)));
        var third = calendar.Preview(second.NextCycleIndex!.Value, 2, new(Utc(2026, 1, 5)));

        Assert.Equal([0, 1], first.Cycles.Select(cycle => cycle.Index));
        Assert.Equal([2, 3], second.Cycles.Select(cycle => cycle.Index));
        Assert.Equal([4], third.Cycles.Select(cycle => cycle.Index));
        Assert.True(first.HasMoreDueCycles);
        Assert.True(second.HasMoreDueCycles);
        Assert.False(third.HasMoreDueCycles);
        Assert.Equal(5, third.NextCycleIndex);
        Assert.Equal(Utc(2026, 1, 5), third.Cycles[0].DueAt);
    }

    [Fact]
    public void Preview_EndDateBoundsScheduledDueTimeInclusively()
    {
        var calendar = Calendar(new DateTime(2026, 1, 1, 9, 0, 0));

        var preview = calendar.Preview(0, 10, new(Utc(2026, 3, 1), Utc(2026, 1, 2)));

        Assert.Equal([0, 1], preview.Cycles.Select(cycle => cycle.Index));
        Assert.Null(preview.NextCycleIndex);
        Assert.False(preview.HasMoreDueCycles);
    }

    [Fact]
    public void Preview_FutureCycleDoesNotAdvanceCursor()
    {
        var calendar = Calendar(new DateTime(2026, 1, 1, 9, 0, 0));

        var preview = calendar.Preview(0, 5, new(Utc(2025, 12, 31)));

        Assert.Empty(preview.Cycles);
        Assert.Equal(0, preview.NextCycleIndex);
        Assert.False(preview.HasMoreDueCycles);
    }

    [Theory]
    [InlineData(ProgrammeIntervalUnit.Day)]
    [InlineData(ProgrammeIntervalUnit.Week)]
    [InlineData(ProgrammeIntervalUnit.Month)]
    [InlineData(ProgrammeIntervalUnit.Year)]
    public void OutOfCalendarRange_IsExhaustedWithoutOverflow(ProgrammeIntervalUnit unit)
    {
        var calendar = new ProgrammeCalendar(new DateTime(9999, 12, 31, 9, 0, 0), new(unit, int.MaxValue), "Etc/UTC");

        Assert.Null(calendar.GetDueAt(int.MaxValue));
        Assert.Null(calendar.GetDueAfterCompletion(Utc(9999, 12, 31)));
        var preview = calendar.Preview(0, 2, new(DateTimeOffset.MaxValue));
        Assert.Single(preview.Cycles);
        Assert.Null(preview.NextCycleIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IntervalMustBePositive(int interval)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProgrammeCalendar(
            new DateTime(2026, 1, 1), new(ProgrammeIntervalUnit.Day, interval), "Etc/UTC"));
    }

    [Fact]
    public void ScheduleRejectsUndefinedUnitAndNonLocalAnchor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Calendar(
            new DateTime(2026, 1, 1), (ProgrammeIntervalUnit)99));
        Assert.Throws<ArgumentException>(() => Calendar(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Throws<TimeZoneNotFoundException>(() => Calendar(
            new DateTime(2026, 1, 1), zone: "Honua/Unknown"));
        Assert.Throws<ArgumentException>(() => Calendar(
            new DateTime(2026, 1, 1), zone: "Eastern Standard Time"));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public void PreviewRejectsUnboundedOrInvalidRequests(int firstCycle, int count)
    {
        var calendar = Calendar(new DateTime(2026, 1, 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => calendar.Preview(firstCycle, count, new(Utc(2026, 1, 1))));
    }

    [Fact]
    public void NegativeCycleIsRejected()
    {
        var calendar = Calendar(new DateTime(2026, 1, 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => calendar.GetDueAt(-1));
    }

    private static ProgrammeCalendar Calendar(DateTime anchor,
        ProgrammeIntervalUnit unit = ProgrammeIntervalUnit.Day, string zone = "Etc/UTC")
        => new(anchor, new(unit), zone);

    private static DateTimeOffset Utc(int year, int month, int day)
        => new(year, month, day, 9, 0, 0, TimeSpan.Zero);
}

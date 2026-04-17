// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Tests.Features.Orchestration;

public sealed class CronExpressionTests
{
    [Theory]
    [InlineData("* * * * *")]
    [InlineData("0 0 * * *")]
    [InlineData("*/15 * * * *")]
    [InlineData("30 2 * * 0")]
    [InlineData("0 9-17 * * 1-5")]
    [InlineData("0 0 1 1 *")]
    public void Parse_AcceptsValidExpressions(string expression)
    {
        var parsed = CronExpression.Parse(expression);

        Assert.Equal(expression, parsed.Expression);
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("*/0 * * * *")]
    [InlineData("5-2 * * * *")]
    public void Parse_RejectsInvalidExpressions(string expression)
    {
        Assert.ThrowsAny<Exception>(() => CronExpression.Parse(expression));
    }

    [Fact]
    public void GetNextOccurrence_ReturnsNextMinuteForStarEverything()
    {
        var cron = CronExpression.Parse("* * * * *");
        var start = new DateTimeOffset(2026, 4, 16, 12, 30, 15, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(start, TimeZoneInfo.Utc);

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2026, 4, 16, 12, 31, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void GetNextOccurrence_RespectsHourAndMinute()
    {
        var cron = CronExpression.Parse("15 9 * * *");
        var start = new DateTimeOffset(2026, 4, 16, 10, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(start, TimeZoneInfo.Utc);

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2026, 4, 17, 9, 15, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void GetNextOccurrence_HandlesStepWildcard()
    {
        var cron = CronExpression.Parse("*/15 * * * *");
        var start = new DateTimeOffset(2026, 4, 16, 12, 7, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(start, TimeZoneInfo.Utc);

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2026, 4, 16, 12, 15, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void GetNextOccurrence_ReturnsNullForUnreachableSchedule()
    {
        // Feb 30 does not exist.
        var cron = CronExpression.Parse("0 0 30 2 *");
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(start, TimeZoneInfo.Utc);

        Assert.Null(next);
    }

    [Fact]
    public void GetNextOccurrence_OrsDayOfMonthAndDayOfWeek_WhenBothRestricted()
    {
        // "At 00:00 on day-of-month 1 or on Sunday" — POSIX OR semantics.
        // After Apr 16, 2026 (Thursday) the next firing should be the earliest of Sunday
        // Apr 19 or the 1st of the next month. Sunday Apr 19 wins.
        var cron = CronExpression.Parse("0 0 1 * 0");
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(start, TimeZoneInfo.Utc);

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2026, 4, 19, 0, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void GetNextOccurrence_OrsDayOfMonthAndDayOfWeek_SelectsFirstOfMonthWhenCloser()
    {
        // After Sunday Apr 26, 2026 the next occurrence should be May 1 (DOM match)
        // before the following Sunday May 3 (DOW match).
        var cron = CronExpression.Parse("0 0 1 * 0");
        var start = new DateTimeOffset(2026, 4, 26, 12, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(start, TimeZoneInfo.Utc);

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void GetNextOccurrence_UsesIntersection_WhenDayOfWeekWildcarded()
    {
        // DOW unrestricted (*) ⇒ plain AND semantics: require DOM == 15 regardless of weekday.
        var cron = CronExpression.Parse("0 0 15 * *");
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(start, TimeZoneInfo.Utc);

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void GetNextOccurrence_FindsLeapDayScheduleAcrossMultiYearGap()
    {
        var cron = CronExpression.Parse("0 0 29 2 *");
        var start = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(start, TimeZoneInfo.Utc);

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2028, 2, 29, 0, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void GetNextOccurrence_FindsLeapDayScheduleFromLeapYear()
    {
        var cron = CronExpression.Parse("0 0 29 2 *");
        var start = new DateTimeOffset(2028, 2, 28, 0, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(start, TimeZoneInfo.Utc);

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2028, 2, 29, 0, 0, 0, TimeSpan.Zero), next);
    }
}

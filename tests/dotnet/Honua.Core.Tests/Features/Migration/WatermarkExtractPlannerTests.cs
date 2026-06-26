// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Migration.Watermark;

namespace Honua.Core.Tests.Features.Migration;

public sealed class WatermarkExtractPlannerTests
{
    private static SourceWatermark TimestampWatermark(DateTimeOffset instant) => new()
    {
        PipelineId = "p",
        SourceId = "s",
        Kind = WatermarkKind.EditTimestamp,
        Value = SourceWatermark.Encode(instant)
    };

    [Fact]
    public void PlanEsri_NullWatermark_PlansFullPull()
    {
        var query = WatermarkExtractPlanner.PlanEsri(watermark: null, editField: "last_edited_date");

        query.IsIncremental.Should().BeFalse();
        query.WhereClause.Should().Be("1=1");
        query.SinceEpochMilliseconds.Should().BeNull();
    }

    [Fact]
    public void PlanEsri_WithWatermark_PlansChangedSincePredicate()
    {
        var since = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var query = WatermarkExtractPlanner.PlanEsri(TimestampWatermark(since), "last_edited_date");

        query.IsIncremental.Should().BeTrue();
        query.SinceEpochMilliseconds.Should().Be(since.ToUnixTimeMilliseconds());
        query.WhereClause.Should().Be($"last_edited_date > {since.ToUnixTimeMilliseconds()}");
    }

    [Fact]
    public void PlanEsri_MissingEditField_FallsBackToFullPull()
    {
        var since = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var query = WatermarkExtractPlanner.PlanEsri(TimestampWatermark(since), editField: null);

        query.IsIncremental.Should().BeFalse();
        query.WhereClause.Should().Be("1=1");
    }

    [Fact]
    public void PlanOgc_WithWatermark_PlansHalfOpenIntervalAfterMark()
    {
        var since = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var filter = WatermarkExtractPlanner.PlanOgc(TimestampWatermark(since), datetimeField: "updated");

        filter.IsIncremental.Should().BeTrue();
        filter.DatetimeField.Should().Be("updated");
        // Lower bound nudged forward 1ms so the boundary record is not re-pulled, interval open-ended.
        filter.DatetimeParameter.Should().StartWith(
            since.AddMilliseconds(1).ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        filter.DatetimeParameter.Should().EndWith("/..");
    }

    [Fact]
    public void PlanOgc_NullWatermark_PlansFullPull()
    {
        var filter = WatermarkExtractPlanner.PlanOgc(watermark: null);

        filter.IsIncremental.Should().BeFalse();
        filter.DatetimeParameter.Should().BeNull();
    }

    [Fact]
    public void ShouldPullFile_NullWatermark_PullsEveryFile()
    {
        WatermarkExtractPlanner.ShouldPullFile(watermark: null, DateTimeOffset.UnixEpoch)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldPullFile_OnlyPullsFilesNewerThanMark()
    {
        var mark = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var watermark = new SourceWatermark
        {
            PipelineId = "p",
            SourceId = "s",
            Kind = WatermarkKind.FileModifiedTime,
            Value = SourceWatermark.Encode(mark)
        };

        WatermarkExtractPlanner.ShouldPullFile(watermark, mark.AddSeconds(1)).Should().BeTrue();
        WatermarkExtractPlanner.ShouldPullFile(watermark, mark).Should().BeFalse();
        WatermarkExtractPlanner.ShouldPullFile(watermark, mark.AddSeconds(-1)).Should().BeFalse();
    }

    [Fact]
    public void Advance_OnlyMovesForward_NeverRewinds()
    {
        var now = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var current = TimestampWatermark(new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero));

        // Newer observed timestamp advances the mark.
        var advanced = WatermarkExtractPlanner.Advance(current, new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero), now);
        advanced.AsTimestamp().Should().Be(new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero));
        advanced.UpdatedAt.Should().Be(now);

        // Older observed timestamp must NOT rewind the mark.
        var notRewound = WatermarkExtractPlanner.Advance(advanced, new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero), now);
        notRewound.AsTimestamp().Should().Be(new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero));

        // No observed records retains the mark but refreshes UpdatedAt.
        var noRecords = WatermarkExtractPlanner.Advance(advanced, maxObservedTimestamp: null, now);
        noRecords.AsTimestamp().Should().Be(new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void SourceWatermark_RoundTripsTimestampThroughEncode()
    {
        var instant = new DateTimeOffset(2026, 6, 1, 9, 30, 15, TimeSpan.Zero);
        var watermark = TimestampWatermark(instant);

        watermark.AsTimestamp().Should().Be(instant);
    }

    [Fact]
    public void SourceWatermark_NullOrUnparseableValue_TreatedAsNoLowerBound()
    {
        var watermark = new SourceWatermark
        {
            PipelineId = "p",
            SourceId = "s",
            Kind = WatermarkKind.EditTimestamp,
            Value = null
        };

        watermark.AsTimestamp().Should().BeNull();
    }
}

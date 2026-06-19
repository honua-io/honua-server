// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Raster.ZarrParser;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class CfTimeAxisIndexerTests
{
    // Axis: 5 daily samples, 2026-01-01 .. 2026-01-05 (step = 1 day).
    private static readonly DateTimeOffset AxisStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AxisEnd = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
    private const long FiveSamples = 5;

    [Theory]
    [InlineData("2026-01-01T00:00:00Z", 0)]
    [InlineData("2026-01-03T00:00:00Z", 2)]
    [InlineData("2026-01-05T00:00:00Z", 4)]
    [InlineData("2026-01-02T10:00:00Z", 1)] // rounds down to nearest (10h < 12h)
    [InlineData("2026-01-02T14:00:00Z", 2)] // rounds up to nearest (14h > 12h)
    public void Instant_RoundsToNearestIndex(string instant, int expectedIndex)
    {
        var t = DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisEnd, FiveSamples, t, t, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(expectedIndex);
        high.Should().Be(expectedIndex);
    }

    [Fact]
    public void Interval_UsesCeilForLowAndFloorForHigh()
    {
        // [2026-01-01T12:00Z, 2026-01-04T12:00Z] -> low=ceil(0.5)=1, high=floor(3.5)=3
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2026, 1, 4, 12, 0, 0, TimeSpan.Zero);

        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisEnd, FiveSamples, t0, t1, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(1);
        high.Should().Be(3);
    }

    [Fact]
    public void Interval_ExactBoundsSelectFullRange()
    {
        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisEnd, FiveSamples, AxisStart, AxisEnd, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(0);
        high.Should().Be(4);
    }

    [Fact]
    public void OpenEndedStart_SelectsFromIndexZero()
    {
        // ../2026-01-03 -> low=0, high=floor(2)=2
        var t1 = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisEnd, FiveSamples, null, t1, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(0);
        high.Should().Be(2);
    }

    [Fact]
    public void OpenEndedEnd_SelectsToLastIndex()
    {
        // 2026-01-03/.. -> low=2, high=lastIndex=4
        var t0 = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisEnd, FiveSamples, t0, null, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(2);
        high.Should().Be(4);
    }

    [Fact]
    public void Instant_OutOfRange_ReturnsError()
    {
        var t = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisEnd, FiveSamples, t, t, out _, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public void Interval_BeforeAxis_ReturnsError()
    {
        var t0 = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisEnd, FiveSamples, t0, t1, out _, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public void Interval_AfterAxis_ReturnsError()
    {
        var t0 = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisEnd, FiveSamples, t0, t1, out _, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public void Interval_SpanningAxis_ClampsToFullRange()
    {
        var t0 = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisEnd, FiveSamples, t0, t1, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(0);
        high.Should().Be(4);
    }

    [Fact]
    public void SingleSample_BracketingRequest_SelectsIndexZero()
    {
        var t0 = new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisStart, stepCount: 1, t0, t1, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(0);
        high.Should().Be(0);
    }

    [Fact]
    public void SingleSample_ExactInstant_SelectsIndexZero()
    {
        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisStart, stepCount: 1, AxisStart, AxisStart, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(0);
        high.Should().Be(0);
    }

    [Fact]
    public void SingleSample_NonIntersectingInterval_ReturnsError()
    {
        var t0 = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisStart, stepCount: 1, t0, t1, out _, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public void ZeroStepCount_ReturnsError()
    {
        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisEnd, stepCount: 0, AxisStart, AxisEnd, out _, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public void NonPositiveSpacing_ReturnsError()
    {
        // start == end with more than one declared sample -> spacing is zero/unknown.
        var ok = CfTimeAxisIndexer.TryResolveTimeIndexRange(
            AxisStart, AxisStart, stepCount: 5, AxisStart, AxisStart, out _, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
    }
}

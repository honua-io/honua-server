// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class CfCoordinateAxisIndexerTests
{
    private static ZarrAxis EvenlySpaced(double start, double end, long count)
        => new("elevation", count, Coordinates: null, start, end);

    private static ZarrAxis Explicit(params double[] coordinates)
        => new("level", coordinates.Length, coordinates, coordinates[0], coordinates[^1]);

    // ---- Evenly spaced, ascending: 0, 250, 500, 750, 1000 (5 samples) ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(500, 2)]
    [InlineData(1000, 4)]
    [InlineData(240, 1)]   // rounds to nearest (240 closer to 250 than 0)
    [InlineData(120, 0)]   // rounds down (120 closer to 0 than 250)
    public void Instant_EvenlySpaced_RoundsToNearest(double value, int expected)
    {
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(
            EvenlySpaced(0, 1000, 5), value, value, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(expected);
        high.Should().Be(expected);
    }

    [Fact]
    public void Interval_EvenlySpaced_UsesCeilForLowFloorForHigh()
    {
        // [120, 760] over {0,250,500,750,1000}: low=ceil(0.48)=1, high=floor(3.04)=3
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(
            EvenlySpaced(0, 1000, 5), 120, 760, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(1);
        high.Should().Be(3);
    }

    [Fact]
    public void Interval_ExactBounds_SelectsFullRange()
    {
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(
            EvenlySpaced(0, 1000, 5), 0, 1000, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(0);
        high.Should().Be(4);
    }

    [Fact]
    public void OpenEnded_Low_SelectsFromStart()
    {
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(
            EvenlySpaced(0, 1000, 5), null, 500, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(0);
        high.Should().Be(2);
    }

    [Fact]
    public void Instant_OutsideAxis_IsRejected()
    {
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(
            EvenlySpaced(0, 1000, 5), 5000, 5000, out _, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
    }

    // ---- Evenly spaced, descending: pressure 1000, 850, 700, 500 (hPa) ----

    [Theory]
    [InlineData(1000, 0)]
    [InlineData(850, 1)]
    [InlineData(500, 3)]
    public void Instant_DescendingAxis_ResolvesIndex(double value, int expected)
    {
        var axis = new ZarrAxis("pressure", 4, Coordinates: null, 1000, 500);
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(axis, value, value, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(expected);
        high.Should().Be(expected);
    }

    [Fact]
    public void Interval_DescendingAxis_SelectsContiguousIndices()
    {
        // Pressure axis 1000,833.33,666.67,500 (4 samples, step -166.67).
        // Request [600, 900] -> indices for coords within span: 833.33 (idx1), 666.67 (idx2).
        var axis = new ZarrAxis("pressure", 4, Coordinates: null, 1000, 500);
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(axis, 600, 900, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(1);
        high.Should().Be(2);
    }

    // ---- Explicit (irregular) coordinate array ----

    [Theory]
    [InlineData(2, 0)]
    [InlineData(10, 1)]
    [InlineData(50, 2)]
    [InlineData(200, 3)]
    public void Instant_Explicit_SelectsNearest(double value, int expected)
    {
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(
            Explicit(2, 10, 50, 200), value, value, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(expected);
        high.Should().Be(expected);
    }

    [Fact]
    public void Interval_Explicit_SelectsCoveredSpan()
    {
        // {2,10,50,200}; [5, 100] covers 10 (idx1) and 50 (idx2).
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(
            Explicit(2, 10, 50, 200), 5, 100, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(1);
        high.Should().Be(2);
    }

    [Fact]
    public void Interval_Explicit_NoIntersection_IsRejected()
    {
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(
            Explicit(2, 10, 50, 200), 500, 600, out _, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public void NonMonotonic_Explicit_IsRejected()
    {
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(
            Explicit(2, 10, 5, 200), 5, 10, out _, out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("monotonic");
    }

    [Fact]
    public void InvertedBounds_AreRejected()
    {
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(
            EvenlySpaced(0, 1000, 5), 800, 200, out _, out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("low <= high");
    }

    [Fact]
    public void SingleSampleAxis_InstantOnSample_IsAccepted()
    {
        var axis = new ZarrAxis("z", 1, Coordinates: null, 42, 42);
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(axis, 42, 42, out var low, out var high, out var error);

        ok.Should().BeTrue(error);
        low.Should().Be(0);
        high.Should().Be(0);
    }

    [Fact]
    public void SingleSampleAxis_InstantOffSample_IsRejected()
    {
        var axis = new ZarrAxis("z", 1, Coordinates: null, 42, 42);
        var ok = CfCoordinateAxisIndexer.TryResolveIndexRange(axis, 99, 99, out _, out _, out _);

        ok.Should().BeFalse();
    }
}

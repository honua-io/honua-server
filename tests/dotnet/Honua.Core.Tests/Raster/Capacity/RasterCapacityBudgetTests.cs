// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Capacity;

namespace Honua.Core.Tests.Raster.Capacity;

public sealed class RasterCapacityBudgetTests
{
    private static readonly RasterCapacityBudget Budget = new(10, 20, 30, 40, 50);

    public static TheoryData<RasterCapacityWork, RasterCapacityDimension, long, long> ExceededDimensions => new()
    {
        { new RasterCapacityWork(11, 0, 0, 0, 0), RasterCapacityDimension.WebOutputCells, 11, 10 },
        { new RasterCapacityWork(0, 21, 0, 0, 0), RasterCapacityDimension.WebOutputBytes, 21, 20 },
        { new RasterCapacityWork(0, 0, 31, 0, 0), RasterCapacityDimension.ObjectRangeRequests, 31, 30 },
        { new RasterCapacityWork(0, 0, 0, 41, 0), RasterCapacityDimension.ObjectRangeBytes, 41, 40 },
        { new RasterCapacityWork(0, 0, 0, 0, 51), RasterCapacityDimension.PostGisWorkUnits, 51, 50 },
    };

    [Theory]
    [MemberData(nameof(ExceededDimensions))]
    public void TryFindExceededDimension_BudgetsEachWorkClassIndependently(
        RasterCapacityWork work,
        RasterCapacityDimension expectedDimension,
        long expectedRequested,
        long expectedLimit)
    {
        var exceeded = Budget.TryFindExceededDimension(work, out var dimension, out var requested, out var limit);

        exceeded.Should().BeTrue();
        dimension.Should().Be(expectedDimension);
        requested.Should().Be(expectedRequested);
        limit.Should().Be(expectedLimit);
    }

    [Fact]
    public void TryFindExceededDimension_WorkAtEveryLimit_IsAdmitted()
    {
        var exceeded = Budget.TryFindExceededDimension(
            new RasterCapacityWork(10, 20, 30, 40, 50),
            out var dimension,
            out _,
            out _);

        exceeded.Should().BeFalse();
        dimension.Should().Be(RasterCapacityDimension.None);
    }
}

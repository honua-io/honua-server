// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster;

public sealed class OverviewLevelSelectorTests
{
    [UnitTest]
    public void Score_PerfectMatch_ReturnsZero()
    {
        // A 256-pixel tile spanning 256 ground units at 1 unit/pixel is a perfect match.
        var score = OverviewLevelSelector.Score(256, 256, 256, 256, 1, 1);

        score.Should().Be(0);
    }

    [UnitTest]
    public void Score_InvalidInputs_ReturnsMaxValue()
    {
        OverviewLevelSelector.Score(0, 256, 256, 256, 1, 1).Should().Be(double.MaxValue);
        OverviewLevelSelector.Score(256, 256, 256, 256, 0, 1).Should().Be(double.MaxValue);
        OverviewLevelSelector.Score(256, 256, 256, 256, double.NaN, 1).Should().Be(double.MaxValue);
    }

    [UnitTest]
    public void SelectBestIndex_PicksClosestGroundResolution()
    {
        // Tile spans 2560 units across 256 px -> ideal resolution is 10 units/pixel.
        var candidates = new (double, double)[]
        {
            (40, 40), // too coarse
            (10, 10), // exact match
            (2.5, 2.5), // too fine
        };

        var best = OverviewLevelSelector.SelectBestIndex(2560, 2560, 256, 256, candidates);

        best.Should().Be(1);
    }

    [UnitTest]
    public void SelectBestIndex_EmptyCandidates_ReturnsNegativeOne()
    {
        var best = OverviewLevelSelector.SelectBestIndex(256, 256, 256, 256, Array.Empty<(double, double)>());

        best.Should().Be(-1);
    }

    [UnitTest]
    public void SelectBestIndex_AllInvalidResolutions_ReturnsNegativeOne()
    {
        var candidates = new (double, double)[] { (0, 0), (-1, -1) };

        var best = OverviewLevelSelector.SelectBestIndex(256, 256, 256, 256, candidates);

        best.Should().Be(-1);
    }
}

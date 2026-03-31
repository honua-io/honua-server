// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Styling.Domain;
using Honua.Core.Features.Styling.Services;

namespace Honua.Core.Tests.Features.Styling;

/// <summary>
/// Unit tests for classification algorithms used by style suggestions.
/// </summary>
public sealed class ClassificationAlgorithmsTests
{
    // ──────────────────── EqualInterval ────────────────────

    [Fact]
    public void EqualInterval_FiveClasses_ProducesEvenBreaks()
    {
        var breaks = ClassificationAlgorithms.EqualInterval(0, 100, 5);

        breaks.Should().HaveCount(4);
        breaks.Should().BeEquivalentTo([20d, 40d, 60d, 80d]);
    }

    [Fact]
    public void EqualInterval_TwoClasses_ProducesSingleBreak()
    {
        var breaks = ClassificationAlgorithms.EqualInterval(10, 30, 2);

        breaks.Should().HaveCount(1);
        breaks[0].Should().BeApproximately(20d, 1e-10);
    }

    [Fact]
    public void EqualInterval_ClassCountOne_ReturnsSingleBreakAtMax()
    {
        var breaks = ClassificationAlgorithms.EqualInterval(0, 100, 1);

        breaks.Should().HaveCount(1);
        breaks[0].Should().Be(100d);
    }

    [Fact]
    public void EqualInterval_MinEqualsMax_ProducesIdenticalBreaks()
    {
        var breaks = ClassificationAlgorithms.EqualInterval(50, 50, 3);

        breaks.Should().HaveCount(2);
        breaks.Should().AllSatisfy(b => b.Should().Be(50d));
    }

    [Fact]
    public void EqualInterval_NegativeRange_ProducesCorrectBreaks()
    {
        var breaks = ClassificationAlgorithms.EqualInterval(-100, -50, 5);

        breaks.Should().HaveCount(4);
        breaks[0].Should().BeApproximately(-90d, 1e-10);
        breaks[^1].Should().BeApproximately(-60d, 1e-10);
    }

    // ──────────────────── Quantile ────────────────────

    [Fact]
    public void Quantile_EvenDistribution_ProducesExpectedBreaks()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        var breaks = ClassificationAlgorithms.Quantile(sorted, 4);

        // Quartile boundaries: 25th, 50th, 75th percentiles
        breaks.Should().HaveCount(3);
        breaks[0].Should().BeApproximately(3.25, 1e-10);
        breaks[1].Should().BeApproximately(5.5, 1e-10);
        breaks[2].Should().BeApproximately(7.75, 1e-10);
    }

    [Fact]
    public void Quantile_TwoClasses_ProducesMedian()
    {
        double[] sorted = [1, 2, 3, 4, 5];
        var breaks = ClassificationAlgorithms.Quantile(sorted, 2);

        breaks.Should().HaveCount(1);
        breaks[0].Should().BeApproximately(3d, 1e-10);
    }

    [Fact]
    public void Quantile_EmptyArray_ReturnsEmpty()
    {
        var breaks = ClassificationAlgorithms.Quantile(ReadOnlySpan<double>.Empty, 5);

        breaks.Should().BeEmpty();
    }

    [Fact]
    public void Quantile_ClassCountOne_ReturnsEmpty()
    {
        double[] sorted = [1, 2, 3];
        var breaks = ClassificationAlgorithms.Quantile(sorted, 1);

        breaks.Should().BeEmpty();
    }

    [Fact]
    public void Quantile_SingleValue_ReturnsThatValue()
    {
        double[] sorted = [42];
        var breaks = ClassificationAlgorithms.Quantile(sorted, 2);

        breaks.Should().HaveCount(1);
        breaks[0].Should().Be(42d);
    }

    // ──────────────────── NaturalBreaks ────────────────────

    [Fact]
    public void NaturalBreaks_ClearClusters_FindsBoundary()
    {
        // Two clear clusters: [1,2,3] and [100,101,102]
        double[] values = [1, 2, 3, 100, 101, 102];
        var breaks = ClassificationAlgorithms.NaturalBreaks(values, 2);

        breaks.Should().HaveCount(1);
        // Break should be between cluster 1 (max 3) and cluster 2 (min 100)
        breaks[0].Should().BeInRange(3d, 100d);
    }

    [Fact]
    public void NaturalBreaks_ThreeClusters_FindsTwoBoundaries()
    {
        double[] values = [1, 2, 3, 50, 51, 52, 200, 201, 202];
        var breaks = ClassificationAlgorithms.NaturalBreaks(values, 3);

        breaks.Should().HaveCount(2);
        breaks.Should().BeInAscendingOrder();
        // First break between [1-3] and [50-52]
        breaks[0].Should().BeInRange(3d, 50d);
        // Second break between [50-52] and [200-202]
        breaks[1].Should().BeInRange(52d, 200d);
    }

    [Fact]
    public void NaturalBreaks_UniformData_FallsBackToEqualInterval()
    {
        double[] values = [5, 5, 5, 5, 5];
        var breaks = ClassificationAlgorithms.NaturalBreaks(values, 3);

        // Uniform data triggers EqualInterval fallback — all breaks at same value
        breaks.Should().HaveCount(2);
        breaks.Should().AllSatisfy(b => b.Should().Be(5d));
    }

    [Fact]
    public void NaturalBreaks_EmptyArray_ReturnsEmpty()
    {
        var breaks = ClassificationAlgorithms.NaturalBreaks(ReadOnlySpan<double>.Empty, 5);

        breaks.Should().BeEmpty();
    }

    [Fact]
    public void NaturalBreaks_ClassCountOne_ReturnsEmpty()
    {
        double[] values = [1, 2, 3];
        var breaks = ClassificationAlgorithms.NaturalBreaks(values, 1);

        breaks.Should().BeEmpty();
    }

    [Fact]
    public void NaturalBreaks_ClassCountExceedsDataCount_CapsToDataCount()
    {
        double[] values = [10, 20, 30];
        var breaks = ClassificationAlgorithms.NaturalBreaks(values, 10);

        // Capped to k=3 (data count), so 2 breaks
        breaks.Should().HaveCount(2);
        breaks.Should().BeInAscendingOrder();
    }

    [Fact]
    public void NaturalBreaks_UnsortedInput_ProducesValidBreaks()
    {
        // Verify that NaturalBreaks handles unsorted input (it sorts internally)
        double[] values = [102, 1, 51, 3, 200, 50, 2, 52, 201, 202];
        var breaks = ClassificationAlgorithms.NaturalBreaks(values, 3);

        breaks.Should().HaveCount(2);
        breaks.Should().BeInAscendingOrder();
        // Same clusters as the sorted version: [1,2,3], [50,51,52], [200,201,202]
        breaks[0].Should().BeInRange(3d, 50d);
        breaks[1].Should().BeInRange(52d, 200d);
    }

    [Fact]
    public void NaturalBreaks_TwoValues_ProducesSingleBreak()
    {
        double[] values = [10, 90];
        var breaks = ClassificationAlgorithms.NaturalBreaks(values, 2);

        breaks.Should().HaveCount(1);
        breaks[0].Should().BeApproximately(50d, 1e-10); // Midpoint of (10, 90)
    }

    // ──────────────────── Cross-algorithm invariants ────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void AllMethods_ProduceCorrectBreakCount(int classCount)
    {
        double[] sorted = [1, 5, 10, 15, 20, 25, 30, 40, 50, 100];
        var expectedBreaks = classCount - 1;

        ClassificationAlgorithms.EqualInterval(1, 100, classCount)
            .Should().HaveCount(expectedBreaks);
        ClassificationAlgorithms.Quantile(sorted, classCount)
            .Should().HaveCount(expectedBreaks);
        ClassificationAlgorithms.NaturalBreaks(sorted, classCount)
            .Should().HaveCount(expectedBreaks);
    }

    [Fact]
    public void AllMethods_BreaksAreAscending()
    {
        double[] sorted = [2, 5, 8, 12, 18, 25, 33, 42, 55, 70, 88, 100];

        ClassificationAlgorithms.EqualInterval(2, 100, 5)
            .Should().BeInAscendingOrder();
        ClassificationAlgorithms.Quantile(sorted, 5)
            .Should().BeInAscendingOrder();
        ClassificationAlgorithms.NaturalBreaks(sorted, 5)
            .Should().BeInAscendingOrder();
    }

    // ──────────────────── ColorPalette.MaxClassCount ────────────────────

    [Fact]
    public void Palette_MaxClassCount_MatchesLargestKey()
    {
        var palette = new ColorPalette
        {
            Name = "Test",
            Category = PaletteCategory.Sequential,
            Colors = new Dictionary<int, string[]>
            {
                [2] = ["#A", "#B"],
                [5] = ["#A", "#B", "#C", "#D", "#E"],
                [3] = ["#A", "#B", "#C"]
            }
        };

        palette.MaxClassCount.Should().Be(5);
    }

    [Fact]
    public void Palette_GetColors_WithinMax_ReturnsExactColors()
    {
        var palette = ColorPalettes.Viridis;

        palette.GetColors(5).Should().HaveCount(5);
        palette.GetColors(9).Should().HaveCount(9);
        palette.GetColors(9).Distinct().Count().Should().Be(9, "all 9 colors should be unique");
    }

    [Fact]
    public void Palette_GetColors_ExceedingMax_RepeatsLastColor()
    {
        // Verify the problem behavior that ClampClassification prevents at the service layer.
        var palette = ColorPalettes.Viridis;
        var max = palette.MaxClassCount; // 9

        var overshot = palette.GetColors(max + 3);
        overshot.Should().HaveCount(max + 3);
        // Last 4 entries should all be the same (last color repeated)
        overshot[max - 1].Should().Be(overshot[max]);
        overshot[max].Should().Be(overshot[max + 1]);
        overshot[max + 1].Should().Be(overshot[max + 2]);
    }
}

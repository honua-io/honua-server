// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Postgres.Tests.Features.Raster;

[Collection("Unit")]
public sealed class PostgresRasterStoreStretchTests
{
    [UnitTest]
    public void BuildStretchMapAlgebraExpression_LinearlyRescalesWithClamping()
    {
        var expression = PostgresRasterStore.BuildStretchMapAlgebraExpression(10, 210);

        expression.Should().Contain("[rast.val]");
        expression.Should().Contain("LEAST(255.0,");
        expression.Should().Contain("GREATEST(0.0,");
        expression.Should().Contain("10");
        expression.Should().Contain("210");
    }

    [UnitTest]
    public void BuildStretchMapAlgebraExpression_FormatsNegativeBoundsInvariant()
    {
        var expression = PostgresRasterStore.BuildStretchMapAlgebraExpression(-12.5, 87.25);

        // Negative/fractional elevation bounds must round-trip as plain decimals.
        expression.Should().Contain("-12.5");
        expression.Should().Contain("87.25");
        expression.Should().NotContain(",-"); // no thousands separators / locale commas
    }

    [UnitTest]
    public void BuildStretchedRasterExpression_SingleBand_WrapsInMapAlgebra()
    {
        var sql = PostgresRasterStore.BuildStretchedRasterExpression(
            "raster",
            new[] { new StretchBounds(0, 100) });

        sql.Should().StartWith("ST_MapAlgebra(raster, 1, '8BUI',");
        sql.Should().NotContain("ST_AddBand");
    }

    [UnitTest]
    public void BuildStretchedRasterExpression_MultiBand_CombinesWithAddBand()
    {
        var sql = PostgresRasterStore.BuildStretchedRasterExpression(
            "raster",
            new[] { new StretchBounds(0, 100), new StretchBounds(5, 95), new StretchBounds(1, 99) });

        sql.Should().StartWith("ST_AddBand(ST_MapAlgebra(raster, 1, '8BUI',");
        sql.Should().Contain("ST_MapAlgebra(raster, 2, '8BUI',");
        sql.Should().Contain("ST_MapAlgebra(raster, 3, '8BUI',");
        sql.Should().Contain("]::raster[])");
    }

    [UnitTest]
    public void BuildStretchedRasterExpression_NoBounds_ReturnsBaseExpression()
    {
        var sql = PostgresRasterStore.BuildStretchedRasterExpression("raster", Array.Empty<StretchBounds>());

        sql.Should().Be("raster");
    }

    [UnitTest]
    public void ResolveStretchBounds_MinMax_UsesStatisticsRange()
    {
        var stretch = new RasterStretch { StretchType = RasterStretchType.MinMax };
        var stat = new RasterStatistics { Band = 1, MinValue = 12, MaxValue = 240 };

        var bounds = PostgresRasterStore.ResolveStretchBounds(stretch, 0, stat, histogram: null);

        bounds.Lo.Should().Be(12);
        bounds.Hi.Should().Be(240);
    }

    [UnitTest]
    public void ResolveStretchBounds_StandardDeviation_ClampsToStatisticsRange()
    {
        var stretch = new RasterStretch
        {
            StretchType = RasterStretchType.StandardDeviation,
            NumberOfStandardDeviations = 2,
        };
        var stat = new RasterStatistics
        {
            Band = 1,
            MinValue = 50,
            MaxValue = 150,
            MeanValue = 100,
            StandardDeviation = 40,
        };

        var bounds = PostgresRasterStore.ResolveStretchBounds(stretch, 0, stat, histogram: null);

        // mean ± 2σ = [20, 180], clamped to the band's [50, 150].
        bounds.Lo.Should().Be(50);
        bounds.Hi.Should().Be(150);
    }

    [UnitTest]
    public void ResolveStretchBounds_StandardDeviation_WithoutStatisticsRange_IsUnclamped()
    {
        var stretch = new RasterStretch
        {
            StretchType = RasterStretchType.StandardDeviation,
            NumberOfStandardDeviations = 2,
        };
        var stat = new RasterStatistics { Band = 1, MeanValue = 100, StandardDeviation = 40 };

        var bounds = PostgresRasterStore.ResolveStretchBounds(stretch, 0, stat, histogram: null);

        bounds.Lo.Should().Be(20);
        bounds.Hi.Should().Be(180);
    }

    [UnitTest]
    public void ResolveStretchBounds_PercentClip_UsesHistogramPercentiles()
    {
        var stretch = new RasterStretch
        {
            StretchType = RasterStretchType.PercentClip,
            MinPercent = 10,
            MaxPercent = 10,
        };
        var stat = new RasterStatistics { Band = 1, MinValue = 0, MaxValue = 100 };
        var histogram = new RasterHistogram
        {
            Band = 1,
            BinCount = 10,
            Min = 0,
            Max = 100,
            Counts = new long[] { 0, 0, 100, 0, 0, 0, 0, 0, 0, 0 },
        };

        var bounds = PostgresRasterStore.ResolveStretchBounds(stretch, 0, stat, histogram);

        // All mass sits in bin 2 ([20,30)); the 10% low/high cutoffs fall on that bin.
        bounds.Lo.Should().Be(20);
        bounds.Hi.Should().Be(30);
    }

    [UnitTest]
    public void ResolveStretchBounds_PercentClip_WithoutHistogram_FallsBackToMinMax()
    {
        var stretch = new RasterStretch { StretchType = RasterStretchType.PercentClip };
        var stat = new RasterStatistics { Band = 1, MinValue = 4, MaxValue = 96 };

        var bounds = PostgresRasterStore.ResolveStretchBounds(stretch, 0, stat, histogram: null);

        bounds.Lo.Should().Be(4);
        bounds.Hi.Should().Be(96);
    }

    [UnitTest]
    public void ResolveStretchBounds_ExplicitStatisticsOnRule_WinOverBandStatistics()
    {
        var stretch = new RasterStretch
        {
            StretchType = RasterStretchType.MinMax,
            StatisticsMin = new double[] { 30 },
            StatisticsMax = new double[] { 220 },
        };
        var stat = new RasterStatistics { Band = 1, MinValue = 0, MaxValue = 255 };

        var bounds = PostgresRasterStore.ResolveStretchBounds(stretch, 0, stat, histogram: null);

        bounds.Lo.Should().Be(30);
        bounds.Hi.Should().Be(220);
    }

    [UnitTest]
    public void ResolveStretchBounds_DegenerateRange_NudgesHighAboveLow()
    {
        var stretch = new RasterStretch { StretchType = RasterStretchType.MinMax };
        var stat = new RasterStatistics { Band = 1, MinValue = 42, MaxValue = 42 };

        var bounds = PostgresRasterStore.ResolveStretchBounds(stretch, 0, stat, histogram: null);

        bounds.Lo.Should().Be(42);
        bounds.Hi.Should().BeGreaterThan(bounds.Lo);
    }

    [UnitTest]
    public void BuildColormapText_OrdersStopsDescendingByValue()
    {
        var colormap = new RasterColormap
        {
            Entries =
            [
                new RasterColormapEntry(0, 0, 0, 0, 255),
                new RasterColormapEntry(100, 255, 255, 255, 255),
            ],
        };

        var text = PostgresRasterStore.BuildColormapText(colormap);

        var lines = text.Trim().Split('\n');
        lines[0].Should().Be("100 255 255 255 255");
        lines[1].Should().Be("0 0 0 0 255");
    }

    [UnitTest]
    public void BuildColormapExpression_WrapsBandOneInStColorMap()
    {
        var colormap = new RasterColormap
        {
            Entries = [new RasterColormapEntry(0, 1, 2, 3, 255)],
        };

        var sql = PostgresRasterStore.BuildColormapExpression("raster", colormap);

        sql.Should().StartWith("ST_ColorMap(raster, 1, '");
        sql.Should().EndWith("'INTERPOLATE')");
    }

    [UnitTest]
    public void BuildStretchBounds_ResolvesOneBoundsPerBand()
    {
        var stretch = new RasterStretch { StretchType = RasterStretchType.MinMax };
        var stats = new[]
        {
            new RasterStatistics { Band = 1, MinValue = 0, MaxValue = 100 },
            new RasterStatistics { Band = 2, MinValue = 10, MaxValue = 200 },
        };

        var bounds = PostgresRasterStore.BuildStretchBounds(stretch, stats, histograms: null);

        bounds.Should().HaveCount(2);
        bounds[0].Should().Be(new StretchBounds(0, 100));
        bounds[1].Should().Be(new StretchBounds(10, 200));
    }
}

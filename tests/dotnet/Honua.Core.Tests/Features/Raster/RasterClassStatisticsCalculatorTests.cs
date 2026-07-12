// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster;

/// <summary>
/// Verifies the class-signature math against hand-computed expected values on small synthetic
/// pixel sets: per-class count, per-band mean, sample covariance (n-1), and per-band summaries.
/// </summary>
public sealed class RasterClassStatisticsCalculatorTests
{
    [UnitTest]
    public void Compute_TwoBandSample_MatchesHandComputedMeanAndCovariance()
    {
        // Band1 = [1,2,3,4], Band2 = [2,4,6,8]; n = 4.
        var vectors = new RasterBandVectorSet
        {
            Bands = [1, 2],
            Pixels = new[]
            {
                new[] { 1.0, 2.0 },
                new[] { 2.0, 4.0 },
                new[] { 3.0, 6.0 },
                new[] { 4.0, 8.0 },
            },
        };

        var signature = RasterClassStatisticsCalculator.Compute(7, "vegetation", vectors);

        signature.ClassId.Should().Be(7);
        signature.Name.Should().Be("vegetation");
        signature.PixelCount.Should().Be(4);
        signature.Bands.Should().Equal(1, 2);

        // mean1 = 2.5, mean2 = 5.
        signature.Mean[0].Should().BeApproximately(2.5, 1e-9);
        signature.Mean[1].Should().BeApproximately(5.0, 1e-9);

        // Sample covariance (divide by n-1 = 3):
        //   Cov11 = 5/3, Cov22 = 20/3, Cov12 = Cov21 = 10/3.
        signature.Covariance[0][0].Should().BeApproximately(5.0 / 3.0, 1e-9);
        signature.Covariance[1][1].Should().BeApproximately(20.0 / 3.0, 1e-9);
        signature.Covariance[0][1].Should().BeApproximately(10.0 / 3.0, 1e-9);
        signature.Covariance[1][0].Should().BeApproximately(10.0 / 3.0, 1e-9);

        // Per-band summaries.
        signature.BandSummaries[0].Min.Should().Be(1);
        signature.BandSummaries[0].Max.Should().Be(4);
        signature.BandSummaries[0].Mean.Should().BeApproximately(2.5, 1e-9);
        signature.BandSummaries[0].StandardDeviation.Should().BeApproximately(Math.Sqrt(5.0 / 3.0), 1e-9);
        signature.BandSummaries[1].Min.Should().Be(2);
        signature.BandSummaries[1].Max.Should().Be(8);
        signature.BandSummaries[1].StandardDeviation.Should().BeApproximately(Math.Sqrt(20.0 / 3.0), 1e-9);
    }

    [UnitTest]
    public void Compute_ConstantBand_YieldsZeroVarianceSignature()
    {
        // A perfectly uniform class (all pixels identical) has zero variance/covariance.
        var vectors = new RasterBandVectorSet
        {
            Bands = [1],
            Pixels = new[] { new[] { 42.0 }, new[] { 42.0 }, new[] { 42.0 } },
        };

        var signature = RasterClassStatisticsCalculator.Compute(1, null, vectors);

        signature.PixelCount.Should().Be(3);
        signature.Mean[0].Should().Be(42);
        signature.Covariance[0][0].Should().Be(0);
        signature.BandSummaries[0].StandardDeviation.Should().Be(0);
    }

    [UnitTest]
    public void Compute_SinglePixel_ReturnsMeanWithZeroCovariance()
    {
        var vectors = new RasterBandVectorSet
        {
            Bands = [1, 2],
            Pixels = new[] { new[] { 10.0, 20.0 } },
        };

        var signature = RasterClassStatisticsCalculator.Compute(3, null, vectors);

        signature.PixelCount.Should().Be(1);
        signature.Mean.Should().Equal(10.0, 20.0);
        // Sample covariance is undefined for n < 2 — reported as zeros, not NaN.
        signature.Covariance[0][0].Should().Be(0);
        signature.Covariance[1][1].Should().Be(0);
        signature.Covariance[0][1].Should().Be(0);
    }

    [UnitTest]
    public void Compute_EmptyClass_ReturnsZeroCountSignature()
    {
        var signature = RasterClassStatisticsCalculator.Compute(9, "empty", RasterBandVectorSet.Empty([1, 2]));

        signature.PixelCount.Should().Be(0);
        signature.Mean.Should().Equal(0.0, 0.0);
        signature.Covariance[0][0].Should().Be(0);
        signature.BandSummaries[0].Min.Should().Be(0);
        signature.BandSummaries[0].Max.Should().Be(0);
    }
}

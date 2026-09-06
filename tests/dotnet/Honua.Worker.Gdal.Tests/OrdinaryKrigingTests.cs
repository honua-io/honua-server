// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Numerical coverage for the ordinary-kriging predictor backing
/// <c>raster.interpolate-kriging</c>. Every oracle here is a mathematical property of
/// the estimator or a closed-form solution of the small system — never a value copied
/// from a run of the code under test.
/// </summary>
public sealed class OrdinaryKrigingTests
{
    private static readonly KrigingSample[] Corners =
    [
        new(0, 0, 10),
        new(4, 0, 20),
        new(0, 4, 30),
        new(4, 4, 40),
        new(2, 2, 100)
    ];

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(VariogramModel.Spherical)]
    [InlineData(VariogramModel.Exponential)]
    [InlineData(VariogramModel.Gaussian)]
    public void Predict_AtASampleLocation_ReproducesTheObservation(VariogramModel model)
    {
        var variogram = OrdinaryKriging.FitDefaults(Corners, model, nugget: null, sill: null, range: null);
        OrdinaryKriging.TrySolve(Corners, variogram, out var kriging, out var failure)
            .Should().BeTrue(failure);

        foreach (var sample in Corners)
        {
            kriging.Predict(sample.X, sample.Y).Should().BeApproximately(
                sample.Z,
                1e-6,
                $"ordinary kriging is an exact interpolator at ({sample.X}, {sample.Y})");
        }
    }

    [UnitTest]
    public void Predict_WithAPositiveNugget_StaysExactAtSamples()
    {
        // A nugget makes the estimator discontinuous at the data, not inexact: γ(0) is
        // still zero, so the sample value is reproduced. Getting this wrong is the
        // classic kriging implementation bug, hence its own case.
        var variogram = new Variogram(VariogramModel.Spherical, Nugget: 0.5, Sill: 2, Range: 5);
        OrdinaryKriging.TrySolve(Corners, variogram, out var kriging, out var failure)
            .Should().BeTrue(failure);

        kriging.Predict(2, 2).Should().BeApproximately(100, 1e-6);
        kriging.Predict(4, 4).Should().BeApproximately(40, 1e-6);
    }

    [UnitTest]
    public void Predict_ConstantField_ReturnsThatConstantEverywhere()
    {
        // With equal observations the dual system forces b = 0 and m = c, so the surface
        // is exactly constant for ANY valid variogram — the unbiasedness constraint at work.
        KrigingSample[] flat = [new(0, 0, 7), new(3, 1, 7), new(1, 4, 7), new(5, 5, 7)];
        var variogram = OrdinaryKriging.FitDefaults(flat, VariogramModel.Exponential, null, null, null);
        OrdinaryKriging.TrySolve(flat, variogram, out var kriging, out var failure).Should().BeTrue(failure);

        foreach (var (x, y) in new[] { (0d, 0d), (2.5d, 2.5d), (-3d, 9d), (100d, 100d) })
        {
            kriging.Predict(x, y).Should().BeApproximately(7, 1e-9, $"constant field at ({x}, {y})");
        }
    }

    [UnitTest]
    public void Predict_TwoSamples_MatchesTheClosedFormOrdinaryKrigingWeights()
    {
        // For two samples the constrained system reduces to w₂ - w₁ = (γ₀₁ - γ₀₂) / γ₁₂
        // with w₁ + w₂ = 1 — an independent derivation, not the general dual solve.
        KrigingSample[] pair = [new(0, 0, 10), new(4, 0, 30)];
        var variogram = new Variogram(VariogramModel.Spherical, Nugget: 0, Sill: 1, Range: 4);
        OrdinaryKriging.TrySolve(pair, variogram, out var kriging, out var failure).Should().BeTrue(failure);

        foreach (var x in new[] { 0.5, 1d, 2d, 3.25, 4d })
        {
            var difference = (variogram.Evaluate(x) - variogram.Evaluate(Math.Abs(4 - x))) / variogram.Evaluate(4);
            var first = (1 - difference) / 2;
            kriging.Predict(x, 0).Should().BeApproximately((first * 10) + ((1 - first) * 30), 1e-9, $"x = {x}");
        }

        // The midpoint of a symmetric pair is the plain mean.
        kriging.Predict(2, 0).Should().BeApproximately(20, 1e-9);
    }

    [UnitTest]
    public void TrySolve_CoincidentSamples_ReportsASingularSystem()
    {
        KrigingSample[] duplicated = [new(1, 1, 5), new(1, 1, 9), new(4, 4, 2)];
        var variogram = new Variogram(VariogramModel.Spherical, 0, 1, 4);

        OrdinaryKriging.TrySolve(duplicated, variogram, out _, out var failure).Should().BeFalse();
        failure.Should().Contain("coincident sample points");
    }

    [UnitTest]
    public void TrySolve_SingleSample_ReturnsThatSamplesValueEverywhere()
    {
        KrigingSample[] single = [new(2, 2, 42)];
        var variogram = OrdinaryKriging.FitDefaults(single, VariogramModel.Spherical, null, null, null);

        OrdinaryKriging.TrySolve(single, variogram, out var kriging, out var failure).Should().BeTrue(failure);
        kriging.Predict(2, 2).Should().Be(42);
        kriging.Predict(-100, 250).Should().Be(42);
    }

    [UnitTest]
    public void FitDefaults_DerivesAPositiveSillAndRangeFromTheSamples()
    {
        var fitted = OrdinaryKriging.FitDefaults(Corners, VariogramModel.Spherical, null, null, null);

        // Total sill defaults to the sample variance; range to a third of the largest
        // pairwise separation, which for the [0,4]² corners is the diagonal 4√2.
        var mean = Corners.Average(sample => sample.Z);
        var variance = Corners.Average(sample => (sample.Z - mean) * (sample.Z - mean));
        fitted.Nugget.Should().Be(0);
        fitted.Sill.Should().BeApproximately(variance, 1e-9);
        fitted.Range.Should().BeApproximately(Math.Sqrt(32) / 3, 1e-9);
    }

    [UnitTest]
    public void FitDefaults_ZeroVarianceSamples_StillYieldAPositiveSill()
    {
        KrigingSample[] flat = [new(0, 0, 3), new(2, 0, 3)];

        var fitted = OrdinaryKriging.FitDefaults(flat, VariogramModel.Spherical, null, null, null);

        fitted.Sill.Should().BeGreaterThan(0, "a zero sill is not a valid variogram");
    }

    [UnitTest]
    public void Variogram_ReachesTheSillAtTheRange_AndIsZeroAtZeroLag()
    {
        var spherical = new Variogram(VariogramModel.Spherical, Nugget: 0.25, Sill: 2, Range: 10);

        spherical.Evaluate(0).Should().Be(0, "the estimator must stay exact at the data");
        spherical.Evaluate(10).Should().BeApproximately(2, 1e-12);
        spherical.Evaluate(25).Should().BeApproximately(2, 1e-12, "the spherical model is flat beyond its range");
        spherical.Evaluate(1e-9).Should().BeGreaterThan(0.25, "a positive nugget lifts the curve off the origin");
        spherical.Evaluate(4).Should().BeLessThan(spherical.Evaluate(6), "the model is monotone in lag");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("spherical", VariogramModel.Spherical)]
    [InlineData("Exponential", VariogramModel.Exponential)]
    [InlineData(" GAUSSIAN ", VariogramModel.Gaussian)]
    [InlineData("", VariogramModel.Spherical)]
    [InlineData(null, VariogramModel.Spherical)]
    public void TryParseModel_AcceptsTheCatalogDomainCaseInsensitively(string? value, VariogramModel expected)
    {
        OrdinaryKriging.TryParseModel(value, out var model).Should().BeTrue();
        model.Should().Be(expected);
    }

    [UnitTest]
    public void TryParseModel_RejectsAnythingElse()
    {
        OrdinaryKriging.TryParseModel("matern", out _).Should().BeFalse();
    }
}

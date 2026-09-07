// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Ordinary-kriging execution proofs for <c>raster.interpolate-kriging</c> (#3932).
///
/// The prior evidence proved REJECTION: the catalog classified the operation
/// Unavailable and the executor failed every job because stock GDAL has no
/// kriging algorithm. The operation now routes to the bundled NumPy solver, so
/// these cases run the production executor against the pinned production GDAL
/// image over committed point fixtures with a FROZEN variogram and decode the
/// emitted two-band GeoTIFF.
///
/// Every expectation below is derived from the ordinary-kriging normal
/// equations applied to the fixture ordinates — never from a snapshot of the
/// executor's output:
/// <list type="bullet">
/// <item>the two-sample system has a closed-form solution, so both bands are
/// asserted cell-by-cell over the whole grid;</item>
/// <item>ordinary kriging is an exact interpolator, so a sample that falls on a
/// pixel centre must reproduce its value with zero kriging error;</item>
/// <item>the weights sum to one, so a constant field must come back constant;</item>
/// <item>the four-corner configuration is symmetric about the grid centre, so
/// its weights are 1/4 each and both the prediction and the kriging variance at
/// that cell have closed forms.</item>
/// </list>
/// </summary>
public sealed partial class RasterExecutionProofTests
{
    private const string KrigingProcessId = "raster.interpolate-kriging";

    /// <summary>Frozen variogram for the four-corner and five-sample fixtures.</summary>
    private static readonly Variogram SquareVariogram = new("spherical", Nugget: 0, Sill: 2, Range: 10);

    /// <summary>Frozen variogram for the two-sample fixture; carries a nugget.</summary>
    private static readonly Variogram PairVariogram = new("exponential", Nugget: 0.25, Sill: 2, Range: 5);

    [Fact]
    public async Task Kriging_TwoSampleSystem_MatchesTheClosedFormPredictionAndError()
    {
        // kriging-pair.geojson: (0,0)=10 and (4,3)=20, so the envelope is
        // x[0,4] y[0,3] and a 4x3 grid gives unit cells with centres at
        // x 0.5..3.5 and y 2.5/1.5/0.5.
        var output = await ExecuteKriging("kriging-pair.geojson", PairVariogram, width: 4, height: 3);

        AssertGrid(output, 4, 3, 4326, [0, 1, 0, 3, 0, -1], 2);

        (double X, double Y, double Z)[] samples = [(0, 0, 10), (4, 3, 20)];
        var predictions = new double[12];
        var errors = new double[12];
        for (var index = 0; index < 12; index++)
        {
            var x = (index % 4 + 0.5) * 1.0;
            var y = 3 - (index / 4 + 0.5) * 1.0;
            (predictions[index], errors[index]) = ClosedFormPair(samples, x, y, PairVariogram);
        }

        AssertBand(output, 0, predictions, "Float64", double.NaN, tolerance: 1e-9);
        AssertBand(output, 1, errors, "Float64", double.NaN, tolerance: 1e-9);
    }

    [Fact]
    public async Task Kriging_SampleOnAPixelCentre_ReproducesItExactlyWithZeroError()
    {
        // kriging-survey.geojson carries the four corners of x[0,4] y[0,4] plus a
        // fifth sample at (2,2) valued 26. On a 5x5 grid the cell centres are
        // 0.4/1.2/2.0/2.8/3.6, so cell (2,2) — flat index 12 — IS the sample.
        var output = await ExecuteKriging("kriging-survey.geojson", SquareVariogram, width: 5, height: 5);

        AssertGrid(output, 5, 5, 4326, [0, 0.8, 0, 4, 0, -0.8], 2);

        var prediction = Values(output, 0);
        var error = Values(output, 1);

        prediction[12].Should().Be(26,
            "ordinary kriging is an exact interpolator: gamma(0) is zero even with a nugget");
        error[12].Should().Be(0, "the kriging variance vanishes at a sample location");

        // Away from the sample the surface must be uncertain, and the prediction
        // must stay inside the sample hull: ordinary kriging with a monotone
        // variogram cannot invent a value outside [10, 40] here.
        for (var index = 0; index < 25; index++)
        {
            if (index == 12)
            {
                continue;
            }

            error[index].Should().BeGreaterThan(0, $"cell {index} is not a sample location");
            prediction[index].Should().BeInRange(10, 40, $"cell {index} must stay within the sample range");
        }
    }

    [Fact]
    public async Task Kriging_ConstantField_ReturnsTheConstantExactlyBecauseTheWeightsSumToOne()
    {
        var output = await ExecuteKriging("kriging-constant.geojson", new Variogram("gaussian", 0, 3, 6), width: 4, height: 4);

        AssertGrid(output, 4, 4, 4326, [0, 1, 0, 4, 0, -1], 2);
        AssertBand(output, 0, Enumerable.Repeat(7.5, 16).ToArray(), "Float64", double.NaN, tolerance: 1e-12);

        // The kriging error depends only on geometry, so a constant field is not
        // a zero-error field anywhere but at the samples themselves.
        Values(output, 1).Should().OnlyContain(value => value > 0);
    }

    [Fact]
    public async Task Kriging_WithheldValidationPoint_MatchesTheSymmetryOracleAndItsFrozenError()
    {
        // kriging-corners.geojson withholds the (2,2) sample of the survey
        // fixture. The four remaining samples are symmetric about the grid
        // centre, so the unique solution of the kriging system must be invariant
        // under that symmetry: every weight is 1/4.
        var output = await ExecuteKriging("kriging-corners.geojson", SquareVariogram, width: 5, height: 5);

        AssertGrid(output, 5, 5, 4326, [0, 0.8, 0, 4, 0, -0.8], 2);

        var prediction = Values(output, 0)[12];
        var error = Values(output, 1)[12];

        prediction.Should().BeApproximately((10 + 20 + 30 + 40) / 4d, 1e-9,
            "equal weights make the prediction the plain mean of the four corners");

        // sigma^2 = sum(w_i * gamma_0i) + mu with w = 1/4 and, from any row of the
        // system, mu = gamma(2*sqrt2) - (1/4)(2*gamma(4) + gamma(4*sqrt2)).
        var gammaCentre = Gamma(SquareVariogram, 2 * Math.Sqrt(2));
        var gammaSide = Gamma(SquareVariogram, 4);
        var gammaDiagonal = Gamma(SquareVariogram, 4 * Math.Sqrt(2));
        var variance = (2 * gammaCentre) - (0.5 * gammaSide) - (0.25 * gammaDiagonal);
        error.Should().BeApproximately(Math.Sqrt(variance), 1e-9);

        // Withheld-point validation against the survey fixture's committed truth
        // of 26 at (2,2): the residual and the single-point RMSE are frozen.
        const double WithheldTruth = 26;
        var residual = prediction - WithheldTruth;
        residual.Should().BeApproximately(-1, 1e-9);
        Math.Abs(residual).Should().BeApproximately(1, 1e-9);
        // The residual is well inside the surface's own uncertainty budget.
        Math.Abs(residual).Should().BeLessThan(2 * error);
    }

    [Fact]
    public async Task Kriging_OracleRejectsASubstitutedAlgorithmAndADifferentVariogram()
    {
        // Negative control 1: inverse distance weighting over the SAME points and
        // the SAME grid produces a perfectly well-formed GeoTIFF that the kriging
        // oracle must reject.
        var idw = await ExecuteRaster(
            "raster.interpolate-idw",
            ("points", Input("kriging-pair.geojson")),
            ("zField", "value"),
            ("width", "4"),
            ("height", "3"));

        (double X, double Y, double Z)[] samples = [(0, 0, 10), (4, 3, 20)];
        var expected = new double[12];
        for (var index = 0; index < 12; index++)
        {
            var x = (index % 4 + 0.5) * 1.0;
            var y = 3 - (index / 4 + 0.5) * 1.0;
            (expected[index], _) = ClosedFormPair(samples, x, y, PairVariogram);
        }

        idw.GetProperty("bands").GetArrayLength().Should().Be(1,
            "IDW publishes a single band, so it cannot even carry a kriging error");
        var substituted = Values(idw, 0);
        substituted.Should().NotBeEquivalentTo(expected,
            "the kriging oracle must reject a substituted interpolation algorithm");
        Enumerable.Range(0, 12).Count(i => Math.Abs(substituted[i] - expected[i]) > 1e-6)
            .Should().BeGreaterThan(6, "the substituted surface differs on most cells, not just one");

        // Negative control 2: the frozen variogram genuinely drives the surface —
        // a different, equally valid range moves the predictions off the oracle.
        var retuned = await ExecuteKriging(
            "kriging-pair.geojson", PairVariogram with { Range = 50 }, width: 4, height: 3);
        var retunedValues = Values(retuned, 0);
        Enumerable.Range(0, 12).Any(i => Math.Abs(retunedValues[i] - expected[i]) > 1e-6)
            .Should().BeTrue("a changed variogram must change the kriged surface");
    }

    [Fact]
    public async Task Kriging_RejectsAnIllPosedVariogramWithoutPublishingAnArtifact()
    {
        await AssertKrigingRejected(
            new Variogram("spherical", Nugget: 2, Sill: 2, Range: 10), "nugget < sill");
        await AssertKrigingRejected(
            new Variogram("matern", Nugget: 0, Sill: 2, Range: 10), "variogramModel");
    }

    // -------------------------------------------------------------------------
    // Oracle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Isotropic semivariogram, written out from its textbook definition rather
    /// than shared with the production backend. gamma(0) is zero even when the
    /// model carries a nugget.
    /// </summary>
    private static double Gamma(Variogram variogram, double h)
    {
        if (h == 0)
        {
            return 0;
        }

        var ratio = h / variogram.Range;
        var structured = variogram.Model switch
        {
            "spherical" => h >= variogram.Range ? 1 : (1.5 * ratio) - (0.5 * Math.Pow(ratio, 3)),
            "exponential" => 1 - Math.Exp(-3 * ratio),
            "gaussian" => 1 - Math.Exp(-3 * ratio * ratio),
            _ => throw new ArgumentOutOfRangeException(nameof(variogram))
        };

        return variogram.Nugget + ((variogram.Sill - variogram.Nugget) * structured);
    }

    /// <summary>
    /// Closed-form ordinary kriging for exactly two samples. Eliminating the
    /// Lagrange multiplier from the 3x3 system leaves
    /// <c>w2 - w1 = (gamma01 - gamma02) / gamma12</c> with <c>w1 + w2 = 1</c>.
    /// </summary>
    private static (double Prediction, double StandardError) ClosedFormPair(
        (double X, double Y, double Z)[] samples, double x, double y, Variogram variogram)
    {
        var gamma12 = Gamma(variogram, Math.Sqrt(
            Math.Pow(samples[0].X - samples[1].X, 2) + Math.Pow(samples[0].Y - samples[1].Y, 2)));
        var gamma01 = Gamma(variogram, Math.Sqrt(Math.Pow(samples[0].X - x, 2) + Math.Pow(samples[0].Y - y, 2)));
        var gamma02 = Gamma(variogram, Math.Sqrt(Math.Pow(samples[1].X - x, 2) + Math.Pow(samples[1].Y - y, 2)));

        var w2 = (1 + ((gamma01 - gamma02) / gamma12)) / 2;
        var w1 = 1 - w2;
        var lagrange = gamma01 - (gamma12 * w2);
        var variance = (w1 * gamma01) + (w2 * gamma02) + lagrange;
        return (
            (w1 * samples[0].Z) + (w2 * samples[1].Z),
            Math.Sqrt(Math.Max(variance, 0)));
    }

    // -------------------------------------------------------------------------
    // Harness
    // -------------------------------------------------------------------------

    private static double[] Values(JsonElement raster, int bandIndex) =>
        raster.GetProperty("bands")[bandIndex].GetProperty("values")
            .EnumerateArray().Select(value => value.GetDouble()).ToArray();

    private Task<JsonElement> ExecuteKriging(string fixture, Variogram variogram, int width, int height) =>
        ExecuteRaster(KrigingProcessId, KrigingInputs(fixture, variogram, width, height));

    private static (string, string)[] KrigingInputs(string fixture, Variogram variogram, int width, int height) =>
    [
        ("points", Input(fixture)),
        ("zField", "value"),
        ("width", width.ToString(CultureInfo.InvariantCulture)),
        ("height", height.ToString(CultureInfo.InvariantCulture)),
        ("variogramModel", variogram.Model),
        ("nugget", variogram.Nugget.ToString("R", CultureInfo.InvariantCulture)),
        ("sill", variogram.Sill.ToString("R", CultureInfo.InvariantCulture)),
        ("range", variogram.Range.ToString("R", CultureInfo.InvariantCulture)),
    ];

    private async Task AssertKrigingRejected(Variogram variogram, string expectedFragment)
    {
        var (result, context) = await ExecuteRaw(
            KrigingProcessId, KrigingInputs("kriging-pair.geojson", variogram, 4, 3));

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain(expectedFragment);
        context.Artifacts.Should().BeEmpty("a rejected kriging job must publish nothing");
    }

    private sealed record Variogram(string Model, double Nugget, double Sill, double Range);
}

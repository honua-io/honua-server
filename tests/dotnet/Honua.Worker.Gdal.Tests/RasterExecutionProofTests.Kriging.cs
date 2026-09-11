// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Additional ordinary-kriging execution proofs for <c>raster.interpolate-kriging</c>
/// (#3932): withheld-point validation and demonstrated negatives, layered on top of the
/// closed-form exactness proofs in <see cref="RasterExecutionProofTests"/>. Every
/// expectation is derived from the ordinary-kriging normal equations applied to the
/// fixture ordinates by hand — never from a snapshot of the executor's output — and the
/// negative-control cases show the oracle actually rejects a plausible wrong answer, not
/// only accepts a right one.
/// </summary>
public sealed partial class RasterExecutionProofTests
{
    private const string KrigingProcessId = "raster.interpolate-kriging";

    /// <summary>Frozen variogram for the withheld-corner and constant-field fixtures.</summary>
    private static readonly KrigingVariogram SquareVariogram = new("spherical", Nugget: 0, Sill: 2, Range: 10);

    /// <summary>
    /// kriging-survey.geojson carries the four corners of x[0,4] y[0,4] plus a fifth
    /// sample at (2,2) valued 26 — the "withheld truth" the next test cross-validates
    /// against. On a 5x5 grid over that extent the cell centres are 0.4/1.2/2.0/2.8/3.6,
    /// so cell (2,2) (flat index 12) IS the sample: this test proves that fact by running
    /// the executor, rather than asserting it only as a hardcoded literal.
    /// </summary>
    [Fact]
    public async Task Kriging_SurveyFixtureCentreSample_ReproducesTheWithheldTruthValueExactly()
    {
        var output = await ExecuteKriging("kriging-survey.geojson", SquareVariogram, width: 5, height: 5);

        AssertGrid(output, 5, 5, 4326, [0, 0.8, 0, 4, 0, -0.8], 2);

        var prediction = Values(output, 0);
        var error = Values(output, 1);

        prediction[12].Should().BeApproximately(26, 1e-3,
            "ordinary kriging is an exact interpolator: gamma(0) is zero even with a nugget");
        error[12].Should().BeApproximately(0, 1e-3, "the kriging variance vanishes at a sample location");
    }

    /// <summary>
    /// Withholding the survey fixture's centre sample (value 26 at (2,2)) leaves the four
    /// corners, which are symmetric about the grid centre: the unique kriging solution at
    /// that cell must be invariant under that symmetry, so every weight is exactly 1/4 —
    /// giving both the prediction and its standard error closed forms independent of the
    /// executor's cached-factorization primal solve. The withheld truth then turns the
    /// same cell into a single-point cross-validation: the prediction's residual against
    /// it is frozen and must sit well inside the surface's own reported uncertainty.
    /// </summary>
    [Fact]
    public async Task Kriging_WithheldValidationPoint_MatchesTheSymmetryOracleAndFrozenResidual()
    {
        var output = await ExecuteKriging("kriging-corners.geojson", SquareVariogram, width: 5, height: 5);

        AssertGrid(output, 5, 5, 4326, [0, 0.8, 0, 4, 0, -0.8], 2);

        var prediction = Values(output, 0)[12];
        var error = Values(output, 1)[12];

        prediction.Should().BeApproximately((10 + 20 + 30 + 40) / 4d, 1e-3,
            "equal weights make the prediction the plain mean of the four corners");

        // sigma^2 = sum(w_i * gamma_0i) + mu with w = 1/4 and, from any row of the
        // system, mu = gamma(2*sqrt2) - (1/4)(2*gamma(4) + gamma(4*sqrt2)).
        var gammaCentre = Gamma(SquareVariogram, 2 * Math.Sqrt(2));
        var gammaSide = Gamma(SquareVariogram, 4);
        var gammaDiagonal = Gamma(SquareVariogram, 4 * Math.Sqrt(2));
        var variance = (2 * gammaCentre) - (0.5 * gammaSide) - (0.25 * gammaDiagonal);
        error.Should().BeApproximately(Math.Sqrt(variance), 1e-3);

        // Withheld-point validation against the survey fixture's committed truth of 26 at
        // (2,2) (kriging-survey.geojson, the same configuration plus that fifth sample):
        // the residual is frozen and stays well inside the surface's own reported error.
        const double WithheldTruth = 26;
        var residual = prediction - WithheldTruth;
        residual.Should().BeApproximately(-1, 1e-3);
        Math.Abs(residual).Should().BeLessThan(2 * error);
    }

    /// <summary>
    /// Two negative controls the oracle above must reject: substituting IDW for kriging
    /// over the identical points and grid, and re-tuning the frozen variogram's range.
    /// Both produce a well-formed raster; neither survives comparison against the
    /// analytically-derived kriging surface.
    /// </summary>
    [Fact]
    public async Task Kriging_OracleRejectsASubstitutedAlgorithmAndADifferentVariogram()
    {
        var pairVariogram = new KrigingVariogram("exponential", Nugget: 0.25, Sill: 2, Range: 5);

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
            var col = index % 4;
            var row = index / 4;
            var x = (col + 0.5) * 1.0;
            var y = 3 - ((row + 0.5) * 1.0);
            (expected[index], _) = ClosedFormPair(samples, x, y, pairVariogram);
        }

        idw.GetProperty("bands").GetArrayLength().Should().Be(1,
            "IDW publishes a single band, so it cannot even carry a kriging standard error");
        var substituted = Values(idw, 0);
        Enumerable.Range(0, 12).Count(i => Math.Abs(substituted[i] - expected[i]) > 1e-3)
            .Should().BeGreaterThan(6, "the substituted surface must differ from kriging on most cells, not just one");

        // A different, equally-valid range genuinely moves the kriged surface off the
        // frozen oracle — the variogram, not just the point set, drives the result.
        var retuned = await ExecuteKriging("kriging-pair.geojson", pairVariogram with { Range = 50 }, width: 4, height: 3);
        var retunedValues = Values(retuned, 0);
        Enumerable.Range(0, 12).Any(i => Math.Abs(retunedValues[i] - expected[i]) > 1e-3)
            .Should().BeTrue("a changed variogram must change the kriged surface");
    }

    /// <summary>
    /// A constant field reproduces exactly because the ordinary-kriging weights always
    /// sum to one; its kriging standard error, driven purely by sample geometry, is not
    /// zero away from the samples even though the field itself is flat everywhere.
    /// </summary>
    [Fact]
    public async Task Kriging_ConstantField_ReturnsTheConstantExactlyWithNonzeroErrorAwayFromSamples()
    {
        var output = await ExecuteKriging(
            "kriging-constant.geojson", new KrigingVariogram("gaussian", 0, 3, 6), width: 4, height: 4);

        AssertGrid(output, 4, 4, 4326, [0, 1, 0, 4, 0, -1], 2);
        var values = Values(output, 0);
        values.Should().OnlyContain(value => Math.Abs(value - 7.5) < 1e-3);

        Values(output, 1).Should().OnlyContain(value => value > 0);
    }

    /// <summary>An ill-posed variogram (nugget above sill) or an unauthorized model name is rejected before any artifact is published.</summary>
    [Fact]
    public async Task Kriging_RejectsAnIllPosedVariogramWithoutPublishingAnArtifact()
    {
        await AssertKrigingRejected(
            new KrigingVariogram("spherical", Nugget: 2, Sill: 1, Range: 10), "'sill' is the TOTAL sill");
        await AssertKrigingRejected(
            new KrigingVariogram("matern", Nugget: 0, Sill: 2, Range: 10), "'model' must be one of");
    }

    // -------------------------------------------------------------------------
    // Oracle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Isotropic semivariogram, written out from its textbook definition rather than
    /// shared with the production backend. gamma(0) is zero even when the model carries
    /// a nugget.
    /// </summary>
    private static double Gamma(KrigingVariogram variogram, double h)
    {
        if (h <= 0)
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
    /// Closed-form ordinary kriging for exactly two samples. Eliminating the Lagrange
    /// multiplier from the 3x3 system leaves
    /// <c>w2 - w1 = (gamma01 - gamma02) / gamma12</c> with <c>w1 + w2 = 1</c>.
    /// </summary>
    private static (double Prediction, double StandardError) ClosedFormPair(
        (double X, double Y, double Z)[] samples, double x, double y, KrigingVariogram variogram)
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

    private Task<JsonElement> ExecuteKriging(string fixture, KrigingVariogram variogram, int width, int height) =>
        ExecuteRaster(KrigingProcessId, KrigingInputs(fixture, variogram, width, height));

    private static (string, string)[] KrigingInputs(string fixture, KrigingVariogram variogram, int width, int height) =>
    [
        ("points", Input(fixture)),
        ("zField", "value"),
        ("width", width.ToString(CultureInfo.InvariantCulture)),
        ("height", height.ToString(CultureInfo.InvariantCulture)),
        ("model", variogram.Model),
        ("nugget", variogram.Nugget.ToString("R", CultureInfo.InvariantCulture)),
        ("sill", variogram.Sill.ToString("R", CultureInfo.InvariantCulture)),
        ("range", variogram.Range.ToString("R", CultureInfo.InvariantCulture)),
    ];

    private async Task AssertKrigingRejected(KrigingVariogram variogram, string expectedFragment)
    {
        var options = GdalJobFactory.Options(_scratch);
        var executor = new GdalRasterInterpolateJobExecutor(_runner, options, NullLogger<GdalRasterInterpolateJobExecutor>.Instance);
        var job = GdalJobFactory.Job(KrigingProcessId, KrigingInputs("kriging-pair.geojson", variogram, 4, 3));
        var context = new RecordingJobExecutionContext(job.OperationId);

        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain(expectedFragment);
        context.Artifacts.Should().BeEmpty("a rejected kriging job must publish nothing");
    }

    private sealed record KrigingVariogram(string Model, double Nugget, double Sill, double Range);
}

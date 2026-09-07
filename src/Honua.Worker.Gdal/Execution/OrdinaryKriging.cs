// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Ordinary-kriging numerical backend for <c>raster.interpolate-kriging</c> (#3932).
///
/// <para>
/// Stock GDAL <c>gdal_grid</c> has no kriging algorithm, so the operation used to be
/// advertised and then fail. This type is the bundled backend: it solves the ordinary
/// kriging system for a frozen variogram over the supplied samples and returns both the
/// prediction surface and the kriging variance. GDAL still owns the raster write, so
/// the georeferencing and encoding stay on the same native path as every other raster
/// operation.
/// </para>
/// <para>
/// The estimator is the textbook one. For samples <c>z(x_1..x_n)</c> and a target
/// <c>x_0</c> it solves
/// <code>
///   [ G  1 ] [ w ]   [ g0 ]
///   [ 1' 0 ] [ mu ] = [ 1  ]
/// </code>
/// where <c>G_ij = gamma(|x_i - x_j|)</c> and <c>g0_i = gamma(|x_i - x_0|)</c>, then
/// reports <c>z(x_0) = sum(w_i z_i)</c> and <c>var(x_0) = sum(w_i g0_i) + mu</c>.
/// Because <c>gamma(0) = 0</c>, the estimator is exact: predicting at a sample location
/// returns that sample's value with zero variance. With a pure-nugget variogram every
/// weight collapses to <c>1/n</c> and the surface is the sample mean.
/// </para>
/// </summary>
internal static class OrdinaryKriging
{
    /// <summary>Variogram models the operation accepts.</summary>
    internal static readonly IReadOnlyList<string> Models = ["spherical", "exponential", "gaussian"];

    /// <summary>
    /// A frozen variogram: <paramref name="Nugget"/> is the discontinuity at the origin,
    /// <paramref name="Sill"/> the total variance the model reaches, and
    /// <paramref name="Range"/> the lag at which it does so (the practical range for the
    /// asymptotic models, following the usual 3a convention).
    /// </summary>
    internal readonly record struct Variogram(string Model, double Nugget, double Sill, double Range)
    {
        /// <summary>The partial sill: the structured variance above the nugget.</summary>
        public double PartialSill => Sill - Nugget;

        /// <summary>Semivariance at lag <paramref name="h"/>.</summary>
        public double Gamma(double h)
        {
            // gamma(0) = 0 by definition; the nugget is the limit from above. This is
            // what makes the estimator exact at sample locations.
            if (h <= 0)
            {
                return 0;
            }

            var structured = Model switch
            {
                "spherical" => h >= Range
                    ? PartialSill
                    : PartialSill * ((1.5 * (h / Range)) - (0.5 * Math.Pow(h / Range, 3))),
                "exponential" => PartialSill * (1 - Math.Exp(-3 * h / Range)),
                "gaussian" => PartialSill * (1 - Math.Exp(-3 * h * h / (Range * Range))),
                _ => throw new ArgumentOutOfRangeException(nameof(Model), Model, "Unsupported variogram model."),
            };

            return Nugget + structured;
        }
    }

    /// <summary>A single observation.</summary>
    internal readonly record struct Sample(double X, double Y, double Value);

    /// <summary>The interpolated surface: prediction and kriging variance, row-major from the top row.</summary>
    internal sealed record Surface(int Width, int Height, double[] Prediction, double[] Variance);

    /// <summary>
    /// Interpolates <paramref name="samples"/> onto a <paramref name="width"/> x
    /// <paramref name="height"/> grid covering <paramref name="envelope"/>. Cell values are
    /// evaluated at pixel centres.
    /// </summary>
    /// <returns><see langword="false"/> with <paramref name="error"/> set when the kriging
    /// system is singular, which happens when the samples contain duplicate locations.</returns>
    internal static bool TryInterpolate(
        IReadOnlyList<Sample> samples,
        Variogram variogram,
        (double MinX, double MinY, double MaxX, double MaxY) envelope,
        int width,
        int height,
        out Surface? surface,
        out string? error)
    {
        surface = null;
        error = null;

        var n = samples.Count;
        // The Lagrange multiplier adds one row and column to the sample matrix.
        var order = n + 1;
        var matrix = new double[order * order];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                matrix[(i * order) + j] = variogram.Gamma(Distance(samples[i], samples[j]));
            }

            matrix[(i * order) + n] = 1;
            matrix[(n * order) + i] = 1;
        }

        matrix[(n * order) + n] = 0;

        if (!LuDecompose(matrix, order, out var pivot))
        {
            error = "the kriging system is singular; the sample set contains duplicate locations";
            return false;
        }

        var pixelWidth = (envelope.MaxX - envelope.MinX) / width;
        var pixelHeight = (envelope.MaxY - envelope.MinY) / height;
        var prediction = new double[width * height];
        var variance = new double[width * height];
        var rhs = new double[order];

        for (var row = 0; row < height; row++)
        {
            var y = envelope.MaxY - ((row + 0.5) * pixelHeight);
            for (var column = 0; column < width; column++)
            {
                var x = envelope.MinX + ((column + 0.5) * pixelWidth);

                for (var i = 0; i < n; i++)
                {
                    var dx = samples[i].X - x;
                    var dy = samples[i].Y - y;
                    rhs[i] = variogram.Gamma(Math.Sqrt((dx * dx) + (dy * dy)));
                }

                rhs[n] = 1;
                var weights = LuSolve(matrix, order, pivot, rhs);

                double estimate = 0;
                double estimationVariance = weights[n];
                for (var i = 0; i < n; i++)
                {
                    estimate += weights[i] * samples[i].Value;
                    estimationVariance += weights[i] * rhs[i];
                }

                var index = (row * width) + column;
                prediction[index] = estimate;
                // Round-off can drive an exactly-zero variance a hair below zero at a
                // sample location; the estimator's variance is non-negative by definition.
                variance[index] = Math.Max(estimationVariance, 0);
            }
        }

        surface = new Surface(width, height, prediction, variance);
        return true;
    }

    /// <summary>Parses and range-checks a variogram, returning a stable caller-facing error.</summary>
    internal static bool TryCreateVariogram(
        string? model,
        double nugget,
        double sill,
        double range,
        out Variogram variogram,
        out string? error)
    {
        variogram = default;
        error = null;

        var normalized = (model ?? "spherical").Trim().ToLowerInvariant();
        if (!Models.Contains(normalized))
        {
            error = $"'variogramModel' must be one of {string.Join(", ", Models)}; got '{model}'";
            return false;
        }

        if (!double.IsFinite(nugget) || nugget < 0)
        {
            error = "'nugget' must be a finite value >= 0";
            return false;
        }

        if (!double.IsFinite(sill) || sill <= 0)
        {
            error = "'sill' must be a finite value > 0";
            return false;
        }

        if (sill < nugget)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"'sill' ({sill}) must be >= 'nugget' ({nugget}); the sill is the total variance, not the partial sill");
            return false;
        }

        if (!double.IsFinite(range) || range <= 0)
        {
            error = "'range' must be a finite value > 0";
            return false;
        }

        variogram = new Variogram(normalized, nugget, sill, range);
        return true;
    }

    private static double Distance(Sample a, Sample b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// In-place LU decomposition with partial pivoting. The ordinary-kriging matrix is
    /// symmetric but indefinite (the bordered Lagrange row), so a Cholesky factorisation
    /// does not apply.
    /// </summary>
    private static bool LuDecompose(double[] matrix, int order, out int[] pivot)
    {
        pivot = new int[order];
        for (var i = 0; i < order; i++)
        {
            pivot[i] = i;
        }

        for (var column = 0; column < order; column++)
        {
            var best = column;
            var bestMagnitude = Math.Abs(matrix[(column * order) + column]);
            for (var row = column + 1; row < order; row++)
            {
                var magnitude = Math.Abs(matrix[(row * order) + column]);
                if (magnitude > bestMagnitude)
                {
                    best = row;
                    bestMagnitude = magnitude;
                }
            }

            if (bestMagnitude <= 1e-12)
            {
                return false;
            }

            if (best != column)
            {
                (pivot[column], pivot[best]) = (pivot[best], pivot[column]);
                for (var k = 0; k < order; k++)
                {
                    (matrix[(column * order) + k], matrix[(best * order) + k]) =
                        (matrix[(best * order) + k], matrix[(column * order) + k]);
                }
            }

            var diagonal = matrix[(column * order) + column];
            for (var row = column + 1; row < order; row++)
            {
                var factor = matrix[(row * order) + column] / diagonal;
                matrix[(row * order) + column] = factor;
                for (var k = column + 1; k < order; k++)
                {
                    matrix[(row * order) + k] -= factor * matrix[(column * order) + k];
                }
            }
        }

        return true;
    }

    private static double[] LuSolve(double[] lu, int order, int[] pivot, double[] rhs)
    {
        var y = new double[order];
        for (var i = 0; i < order; i++)
        {
            var sum = rhs[pivot[i]];
            for (var j = 0; j < i; j++)
            {
                sum -= lu[(i * order) + j] * y[j];
            }

            y[i] = sum;
        }

        var x = new double[order];
        for (var i = order - 1; i >= 0; i--)
        {
            var sum = y[i];
            for (var j = i + 1; j < order; j++)
            {
                sum -= lu[(i * order) + j] * x[j];
            }

            x[i] = sum / lu[(i * order) + i];
        }

        return x;
    }
}

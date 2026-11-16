// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Worker.Gdal.Execution;

internal enum VariogramModel
{
    Spherical,

    Exponential,

    Gaussian
}

internal readonly record struct KrigingSample(double X, double Y, double Z);

internal readonly record struct Variogram(VariogramModel Model, double Nugget, double Sill, double Range)
{
    public double Evaluate(double h)
    {
        if (h <= 0d)
        {
            return 0d;
        }

        var partialSill = Sill - Nugget;
        var ratio = h / Range;
        var structure = Model switch
        {
            VariogramModel.Spherical => ratio >= 1d ? 1d : (1.5 * ratio) - (0.5 * ratio * ratio * ratio),
            VariogramModel.Exponential => 1d - Math.Exp(-3d * ratio),
            _ => 1d - Math.Exp(-3d * ratio * ratio)
        };

        return Nugget + (partialSill * structure);
    }
}

internal sealed class OrdinaryKriging
{
    private readonly KrigingSample[] _samples;
    private readonly double[] _weights;
    private readonly double _lagrange;
    private readonly Variogram _variogram;
    private readonly double[] _factored;
    private readonly int[] _pivot;
    private readonly int _size;

    private OrdinaryKriging(
        KrigingSample[] samples,
        double[] weights,
        double lagrange,
        Variogram variogram,
        double[] factored,
        int[] pivot,
        int size)
    {
        _samples = samples;
        _weights = weights;
        _lagrange = lagrange;
        _variogram = variogram;
        _factored = factored;
        _pivot = pivot;
        _size = size;
    }

    public Variogram Variogram => _variogram;

    public static bool TrySolve(
        IReadOnlyList<KrigingSample> samples,
        Variogram variogram,
        out OrdinaryKriging kriging,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(samples);
        kriging = null!;
        failure = "";

        if (samples.Count == 0)
        {
            failure = "at least one sample point is required";
            return false;
        }

        var points = samples.ToArray();
        var n = points.Length;
        var size = n + 1;

        // Normalize Γ by its sill so its entries and the fixed constraint entries have
        // comparable scales. The dual weights then absorb the sill; prediction uses
        // the same normalized semivariances and preserves the original value units.
        // Bordered semivariance matrix [Γ/sill 1; 1ᵀ 0], right-hand side [z; 0].
        var matrix = new double[size * size];
        var rhs = new double[size];
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                var gamma = variogram.Evaluate(Distance(points[i], points[j])) / variogram.Sill;
                matrix[(i * size) + j] = gamma;
                matrix[(j * size) + i] = gamma;
            }

            matrix[(i * size) + n] = 1d;
            matrix[(n * size) + i] = 1d;
            rhs[i] = points[i].Z;
        }

        if (!TryFactor(matrix, size, out var pivot))
        {
            // Gamma(0) stays zero for every nugget, so duplicates retain identical rows.
            failure = "the kriging system is singular; consolidate sample points that share a location";
            return false;
        }

        var solution = SolveFactored(matrix, pivot, size, rhs);
        if (!AllFinite(solution))
        {
            failure = "the kriging system is singular; consolidate sample points that share a location";
            return false;
        }

        var weights = new double[n];
        Array.Copy(solution, weights, n);
        kriging = new OrdinaryKriging(points, weights, solution[n], variogram, matrix, pivot, size);
        return true;
    }

    public static Variogram FitDefaults(
        IReadOnlyList<KrigingSample> samples,
        VariogramModel model,
        double? nugget,
        double? sill,
        double? range)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var resolvedNugget = nugget ?? 0d;
        var resolvedSill = sill ?? DefaultSill(samples, resolvedNugget);
        var resolvedRange = range ?? DefaultRange(samples);
        return new Variogram(model, resolvedNugget, resolvedSill, resolvedRange);
    }

    public double Predict(double x, double y) => PredictWithVariance(x, y).Prediction;

    public (double Prediction, double Variance) PredictWithVariance(double x, double y)
    {
        var n = _samples.Length;
        var gamma0 = new double[_size];
        var estimate = _lagrange;
        for (var i = 0; i < n; i++)
        {
            var sample = _samples[i];
            var dx = x - sample.X;
            var dy = y - sample.Y;
            var gamma = _variogram.Evaluate(Math.Sqrt((dx * dx) + (dy * dy))) / _variogram.Sill;
            gamma0[i] = gamma;
            estimate += _weights[i] * gamma;
        }

        gamma0[n] = 1d;

        var primal = SolveFactored(_factored, _pivot, _size, gamma0);
        var normalizedVariance = primal[n];
        for (var i = 0; i < n; i++)
        {
            normalizedVariance += primal[i] * gamma0[i];
        }

        // Γ (and so γ₀) was normalized by the sill to solve the system; the variance
        // scales linearly with the semivariogram, so it is restored the same way.
        var variance = Math.Max(normalizedVariance, 0d) * _variogram.Sill;
        return (estimate, variance);
    }

    private static double DefaultSill(IReadOnlyList<KrigingSample> samples, double nugget)
    {
        double mean = 0d;
        for (var i = 0; i < samples.Count; i++)
        {
            mean += samples[i].Z;
        }

        mean /= samples.Count;

        double variance = 0d;
        for (var i = 0; i < samples.Count; i++)
        {
            var deviation = samples[i].Z - mean;
            variance += deviation * deviation;
        }

        variance /= samples.Count;

        // A zero-variance sample set (every observation equal) still needs a positive
        // sill for γ to be a valid variogram; the prediction is the constant either way.
        return variance > 0d ? nugget + variance : Math.Max(nugget, 0d) + 1d;
    }

    private static double DefaultRange(IReadOnlyList<KrigingSample> samples)
    {
        double maxDistance = 0d;
        for (var i = 0; i < samples.Count; i++)
        {
            for (var j = i + 1; j < samples.Count; j++)
            {
                maxDistance = Math.Max(maxDistance, Distance(samples[i], samples[j]));
            }
        }

        // A single point (or a fully coincident set) has no separation to scale by; any
        // positive range yields the same constant surface, so fall back to unit range.
        return maxDistance > 0d ? maxDistance / 3d : 1d;
    }

    private static double Distance(KrigingSample a, KrigingSample b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static bool TryFactor(double[] matrix, int size, out int[] pivot)
    {
        pivot = new int[size];
        for (var i = 0; i < size; i++)
        {
            pivot[i] = i;
        }

        // Scale the singularity threshold by the magnitude of the system so the test is
        // invariant to the units of the input values (metres vs. degrees vs. counts).
        double scale = 0d;
        for (var i = 0; i < matrix.Length; i++)
        {
            scale = Math.Max(scale, Math.Abs(matrix[i]));
        }

        var tolerance = scale * size * 1e-12;

        for (var column = 0; column < size; column++)
        {
            var pivotRow = column;
            var pivotMagnitude = Math.Abs(matrix[(column * size) + column]);
            for (var row = column + 1; row < size; row++)
            {
                var candidate = Math.Abs(matrix[(row * size) + column]);
                if (candidate > pivotMagnitude)
                {
                    pivotMagnitude = candidate;
                    pivotRow = row;
                }
            }

            if (pivotMagnitude <= tolerance)
            {
                return false;
            }

            if (pivotRow != column)
            {
                for (var k = 0; k < size; k++)
                {
                    (matrix[(column * size) + k], matrix[(pivotRow * size) + k]) =
                        (matrix[(pivotRow * size) + k], matrix[(column * size) + k]);
                }

                (pivot[column], pivot[pivotRow]) = (pivot[pivotRow], pivot[column]);
            }

            var diagonal = matrix[(column * size) + column];
            for (var row = column + 1; row < size; row++)
            {
                var factor = matrix[(row * size) + column] / diagonal;
                matrix[(row * size) + column] = factor;
                // Skip only exact zero: all nonzero factors, however small, must be applied.
                // codeql[cs/equality-on-floats]: a tolerance would drop small elimination factors and change the kriging solution
                if (factor == 0d)
                {
                    continue;
                }

                for (var k = column + 1; k < size; k++)
                {
                    matrix[(row * size) + k] -= factor * matrix[(column * size) + k];
                }
            }
        }

        return true;
    }

    private static double[] SolveFactored(double[] factored, int[] pivot, int size, double[] rhs)
    {
        var x = new double[size];
        for (var i = 0; i < size; i++)
        {
            x[i] = rhs[pivot[i]];
        }

        // Forward substitution: L has an implicit unit diagonal; its multipliers are
        // stored below the diagonal of `factored`.
        for (var i = 0; i < size; i++)
        {
            var sum = x[i];
            for (var j = 0; j < i; j++)
            {
                sum -= factored[(i * size) + j] * x[j];
            }

            x[i] = sum;
        }

        // Back substitution against U (the diagonal and above).
        for (var i = size - 1; i >= 0; i--)
        {
            var sum = x[i];
            for (var j = i + 1; j < size; j++)
            {
                sum -= factored[(i * size) + j] * x[j];
            }

            x[i] = sum / factored[(i * size) + i];
        }

        return x;
    }

    private static bool AllFinite(double[] values)
    {
        foreach (var value in values)
        {
            if (!double.IsFinite(value))
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryParseModel(string? value, out VariogramModel model)
    {
        model = VariogramModel.Spherical;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "spherical":
                model = VariogramModel.Spherical;
                return true;
            case "exponential":
                model = VariogramModel.Exponential;
                return true;
            case "gaussian":
                model = VariogramModel.Gaussian;
                return true;
            default:
                return false;
        }
    }

    public static string ModelName(VariogramModel model)
        => model.ToString().ToLower(CultureInfo.InvariantCulture);
}

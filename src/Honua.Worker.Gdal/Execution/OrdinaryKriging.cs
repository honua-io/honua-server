// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Worker.Gdal.Execution;

/// <summary>Authorized semivariogram model for <c>raster.interpolate-kriging</c>.</summary>
internal enum VariogramModel
{
    /// <summary>Spherical model; reaches the sill exactly at the range.</summary>
    Spherical,

    /// <summary>Exponential model; reaches 95% of the sill at the (practical) range.</summary>
    Exponential,

    /// <summary>Gaussian model; reaches 95% of the sill at the (practical) range.</summary>
    Gaussian
}

/// <summary>One scattered observation feeding the kriging system.</summary>
internal readonly record struct KrigingSample(double X, double Y, double Z);

/// <summary>
/// Isotropic semivariogram γ(h) with a nugget, a total sill and a range.
/// γ(0) is defined as exactly zero even with a positive nugget, which is what makes
/// ordinary kriging an EXACT interpolator: a prediction at a sample location
/// reproduces that sample's value.
/// </summary>
internal readonly record struct Variogram(VariogramModel Model, double Nugget, double Sill, double Range)
{
    /// <summary>Evaluates the semivariance at lag <paramref name="h"/> (>= 0).</summary>
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

/// <summary>
/// Ordinary kriging over a bounded scattered-point sample, in the DUAL formulation.
///
/// <para>
/// The primal ordinary-kriging system solves, per prediction location, for weights
/// <c>w</c> under the unbiasedness constraint <c>Σw = 1</c>:
/// <c>[Γ 1; 1ᵀ 0][w; μ] = [γ₀; 1]</c>, predicting <c>ẑ₀ = zᵀw</c>. Because the bordered
/// matrix <c>K</c> is symmetric, substituting gives the equivalent DUAL form
/// <c>[b; m] = K⁻¹[z; 0]</c> with <c>ẑ₀ = bᵀγ₀ + m</c>: one factorization for the whole
/// grid and O(n) work per cell instead of O(n²). Predictions are identical to the
/// primal system — only the cost changes — which is what makes a full-grid kriging
/// surface a bounded job rather than an O(cells·n³) one.
/// </para>
///
/// <para>
/// Two properties this preserves are used as the executor's analytical oracles:
/// the estimator is EXACT at sample locations (γ(0)=0), and it reproduces a CONSTANT
/// field exactly (for equal sample values <c>b = 0</c>, <c>m = c</c>).
/// </para>
/// </summary>
internal sealed class OrdinaryKriging
{
    private readonly KrigingSample[] _samples;
    private readonly double[] _weights;
    private readonly double _lagrange;
    private readonly Variogram _variogram;

    private OrdinaryKriging(KrigingSample[] samples, double[] weights, double lagrange, Variogram variogram)
    {
        _samples = samples;
        _weights = weights;
        _lagrange = lagrange;
        _variogram = variogram;
    }

    /// <summary>The fitted (or caller-supplied) semivariogram the solve used.</summary>
    public Variogram Variogram => _variogram;

    /// <summary>
    /// Factors the dual ordinary-kriging system for <paramref name="samples"/> under
    /// <paramref name="variogram"/>. Returns <see langword="false"/> with a caller-facing
    /// <paramref name="failure"/> when the system is singular — which for a valid
    /// variogram means coincident sample locations, the one input shape that genuinely
    /// has no unique ordinary-kriging solution.
    /// </summary>
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

        // Bordered semivariance matrix [Γ 1; 1ᵀ 0] with right-hand side [z; 0].
        var matrix = new double[size * size];
        var rhs = new double[size];
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                var gamma = variogram.Evaluate(Distance(points[i], points[j]));
                matrix[(i * size) + j] = gamma;
                matrix[(j * size) + i] = gamma;
            }

            matrix[(i * size) + n] = 1d;
            matrix[(n * size) + i] = 1d;
            rhs[i] = points[i].Z;
        }

        if (!TrySolveInPlace(matrix, rhs, size, out var solution))
        {
            failure = "the kriging system is singular; remove coincident sample points or raise 'nugget'";
            return false;
        }

        var weights = new double[n];
        Array.Copy(solution, weights, n);
        kriging = new OrdinaryKriging(points, weights, solution[n], variogram);
        return true;
    }

    /// <summary>
    /// Derives the default semivariogram from the sample set when the caller does not
    /// pin one: total sill = the sample variance (the a-priori variance the estimator
    /// should reproduce far from data), practical range = one third of the largest
    /// pairwise separation (the standard rule of thumb — beyond that lag the empirical
    /// variogram is estimated from too few pairs to be meaningful), nugget = 0. Every
    /// component is overridable per submission; the defaults only have to be positive,
    /// finite and scale-appropriate, because ordinary kriging's exactness at data
    /// points and its reproduction of a constant field hold for ANY valid variogram.
    /// </summary>
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

    /// <summary>Predicts the surface value at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public double Predict(double x, double y)
    {
        var estimate = _lagrange;
        for (var i = 0; i < _samples.Length; i++)
        {
            var sample = _samples[i];
            var dx = x - sample.X;
            var dy = y - sample.Y;
            estimate += _weights[i] * _variogram.Evaluate(Math.Sqrt((dx * dx) + (dy * dy)));
        }

        return estimate;
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

    /// <summary>
    /// Gaussian elimination with partial pivoting on the row-major
    /// <paramref name="size"/>×<paramref name="size"/> system. The bordered kriging
    /// matrix is symmetric but indefinite, so a Cholesky factorization does not apply;
    /// partial pivoting is the standard stable choice.
    /// </summary>
    private static bool TrySolveInPlace(double[] matrix, double[] rhs, int size, out double[] solution)
    {
        solution = rhs;

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
                for (var k = column; k < size; k++)
                {
                    (matrix[(column * size) + k], matrix[(pivotRow * size) + k]) =
                        (matrix[(pivotRow * size) + k], matrix[(column * size) + k]);
                }

                (rhs[column], rhs[pivotRow]) = (rhs[pivotRow], rhs[column]);
            }

            var pivot = matrix[(column * size) + column];
            for (var row = column + 1; row < size; row++)
            {
                var factor = matrix[(row * size) + column] / pivot;
                if (factor == 0d)
                {
                    continue;
                }

                for (var k = column; k < size; k++)
                {
                    matrix[(row * size) + k] -= factor * matrix[(column * size) + k];
                }

                rhs[row] -= factor * rhs[column];
            }
        }

        for (var row = size - 1; row >= 0; row--)
        {
            var accumulator = rhs[row];
            for (var k = row + 1; k < size; k++)
            {
                accumulator -= matrix[(row * size) + k] * rhs[k];
            }

            rhs[row] = accumulator / matrix[(row * size) + row];
            if (!double.IsFinite(rhs[row]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Parses an authorized variogram model name, case-insensitively.</summary>
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

    /// <summary>Canonical lowercase name of a model, as the catalog enumerates it.</summary>
    public static string ModelName(VariogramModel model)
        => model.ToString().ToLower(CultureInfo.InvariantCulture);
}

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
///
/// <para>
/// The kriging (estimation) VARIANCE at a target has no dual shortcut: unlike the
/// prediction, it needs the PRIMAL weights <c>(w, μ)</c> for that specific target,
/// <c>σ²(x₀) = wᵀγ₀ + μ</c>, solving <c>K[w; μ] = [γ₀; 1]</c>. The factorization from
/// <see cref="TrySolve"/> is kept (not just its one dual solution) so each cell reuses
/// it via forward/back substitution — O(n²) per cell, the standard cost of a pointwise
/// kriging variance map, still far cheaper than re-factoring per cell.
/// </para>
/// </summary>
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
    public double Predict(double x, double y) => PredictWithVariance(x, y).Prediction;

    /// <summary>
    /// Predicts the surface value AND the ordinary-kriging estimation variance at
    /// (<paramref name="x"/>, <paramref name="y"/>). The prediction reuses the cached
    /// dual weights (O(n)); the variance solves the PRIMAL system for this one target
    /// against the cached factorization (O(n²)) — see the type's remarks. Round-off can
    /// drive an exactly-zero variance a hair below zero at a sample location; the
    /// estimator's variance is non-negative by definition, so it is clamped.
    /// </summary>
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

    /// <summary>
    /// In-place LU decomposition with partial pivoting on the row-major
    /// <paramref name="size"/>×<paramref name="size"/> system. The bordered kriging
    /// matrix is symmetric but indefinite, so a Cholesky factorization does not apply;
    /// partial pivoting is the standard stable choice.
    ///
    /// <para>
    /// Unlike a one-shot Gaussian elimination on an augmented <c>[A|b]</c> system, the
    /// multiplier below each pivot is STORED (not eliminated to zero) so the
    /// factorization can be reused for more than one right-hand side via
    /// <see cref="SolveFactored"/> — once for the dual prediction weights in
    /// <see cref="TrySolve"/>, and again per grid cell for the primal variance weights
    /// in <see cref="PredictWithVariance"/>.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Solves <c>K x = rhs</c> against the LU factorization <paramref name="factored"/>
    /// (from <see cref="TryFactor"/>) via forward, then back, substitution. Does not
    /// mutate <paramref name="factored"/> or <paramref name="rhs"/>.
    /// </summary>
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

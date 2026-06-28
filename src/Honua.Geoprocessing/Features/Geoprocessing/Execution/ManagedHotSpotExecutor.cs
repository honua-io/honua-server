// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>analytics.hotspot-managed</c> executor. A job-dispatchable, managed
/// (NetTopologySuite) Hot Spot Analysis that computes the Getis-Ord Gi*
/// statistic for every input feature over an inline FeatureCollection. Like the
/// other managed analytics executors (<see cref="ManagedClusterExecutor"/>,
/// <see cref="ManagedDensityExecutor"/>) it is the workflow/codemod-reachable
/// counterpart that runs against an INLINE FeatureCollection so the lean
/// dispatcher can construct it unconditionally without a Postgres dependency.
///
/// <para>
/// Conceptualization of spatial relationships: a <b>fixed distance band</b>. Two
/// features are neighbours when the Euclidean distance between their point
/// representatives (centroid for non-point inputs) is &lt;= <c>distanceBand</c>
/// CRS units. As this is the Gi* (with-star) statistic, each feature is always
/// its own neighbour. Distances are evaluated in the CRS units of the supplied
/// feature geometries — geodesic conversion is not performed (the same
/// convention used by the managed <c>geometry.buffer</c> and clustering
/// executors).
/// </para>
///
/// <para>
/// For feature <c>i</c> with analysis value <c>x</c>, global mean
/// <c>X̄</c>, global population standard deviation <c>S</c>, total feature
/// count <c>n</c>, and binary fixed-distance weights <c>w_ij ∈ {0,1}</c> (so
/// <c>Σ w_ij = Σ w_ij² = W_i</c>, the neighbour count including self):
/// <code>
/// Gi* = (Σ_j w_ij x_j - X̄·W_i) / (S · sqrt((n·W_i - W_i²) / (n - 1)))
/// </code>
/// The Gi* statistic is a z-score; the two-tailed p-value is
/// <c>2·(1 - Φ(|z|))</c> where <c>Φ</c> is the standard-normal CDF. Each output
/// feature preserves its input geometry and attributes and carries:
/// <list type="bullet">
///   <item><c>GI_ZSCORE</c>: the Gi* z-score.</item>
///   <item><c>GI_PVALUE</c>: the two-tailed p-value.</item>
///   <item><c>GI_BIN</c>: the Esri-style confidence bin in [-3, 3] — sign from
///   the z-score (hot = positive, cold = negative) and magnitude from the
///   significance level (3 = 99%, 2 = 95%, 1 = 90%, 0 = not significant).</item>
/// </list>
/// </para>
///
/// <para>
/// Degenerate inputs are rejected with a clear error: fewer than two located
/// features (Gi* is undefined for a single point) and a zero-variance analysis
/// field (all-identical values). Features with null/empty geometry are dropped
/// before analysis and are not emitted. A feature whose neighbourhood spans the
/// entire dataset (<c>W_i == n</c>) has an undefined local variance term and is
/// emitted with a neutral z-score of 0, p-value 1, and bin 0.
/// </para>
/// </summary>
internal sealed class ManagedHotSpotExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "analytics.hotspot-managed";
    internal const string ZScoreAttribute = "GI_ZSCORE";
    internal const string PValueAttribute = "GI_PVALUE";
    internal const string BinAttribute = "GI_BIN";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var field = inputs.Require("field").Trim();
        if (field.Length == 0)
        {
            throw new TransformInputException("'field' must be a non-empty attribute name");
        }

        var distanceBand = ReadDistanceBand(inputs);
        var points = MaterializePoints(source, field);

        if (points.Count < 2)
        {
            throw new TransformInputException(
                "Hot Spot Analysis requires at least two features with a located geometry and a numeric analysis value");
        }

        var n = points.Count;

        double sum = 0;
        double sumSquares = 0;
        foreach (var point in points)
        {
            sum += point.Value;
            sumSquares += point.Value * point.Value;
        }

        var mean = sum / n;
        // Population standard deviation (matches the Getis-Ord Gi* definition).
        var variance = (sumSquares / n) - (mean * mean);
        if (variance <= 0)
        {
            throw new TransformInputException(
                $"Hot Spot Analysis requires variation in '{field}'; all analysis values are identical (zero variance)");
        }

        var standardDeviation = Math.Sqrt(variance);

        var index = BuildIndex(points, distanceBand, cancellationToken);

        var output = new List<IFeature>(n);
        for (var i = 0; i < n; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (neighbourValueSum, neighbourCount) = SumNeighbours(index, points, i, distanceBand);
            var (zScore, pValue, bin) = ComputeStatistic(
                neighbourValueSum, neighbourCount, n, mean, standardDeviation);

            output.Add(WithStatistics(points[i].SourceFeature, zScore, pValue, bin));
        }

        return output;
    }

    /// <summary>
    /// Computes the Getis-Ord Gi* z-score, two-tailed p-value, and confidence
    /// bin for a single feature given the sum of analysis values over its
    /// fixed-distance neighbourhood (including itself) and the neighbour count.
    /// </summary>
    private static (double ZScore, double PValue, int Bin) ComputeStatistic(
        double neighbourValueSum,
        int neighbourCount,
        int n,
        double mean,
        double standardDeviation)
    {
        // Binary weights: Σ w_ij = Σ w_ij² = W_i (the neighbour count incl. self).
        double weightSum = neighbourCount;

        // (n·W - W²)/(n-1) is the local variance term. It is zero when the
        // neighbourhood is the whole dataset (W == n): no local contrast exists,
        // so the statistic is neutral rather than a divide-by-zero NaN.
        var localVariance = ((n * weightSum) - (weightSum * weightSum)) / (n - 1);
        if (localVariance <= 0)
        {
            return (0d, 1d, 0);
        }

        var numerator = neighbourValueSum - (mean * weightSum);
        var denominator = standardDeviation * Math.Sqrt(localVariance);
        var zScore = numerator / denominator;

        var pValue = TwoTailedPValue(zScore);
        var bin = ConfidenceBin(zScore, pValue);
        return (zScore, pValue, bin);
    }

    /// <summary>
    /// Two-tailed standard-normal p-value <c>2·(1 - Φ(|z|)) = erfc(|z|/√2)</c>,
    /// clamped to [0, 1].
    /// </summary>
    private static double TwoTailedPValue(double zScore)
    {
        var p = Erfc(Math.Abs(zScore) / Math.Sqrt(2.0));
        return Math.Clamp(p, 0d, 1d);
    }

    /// <summary>
    /// Esri-style Gi_Bin: sign from the z-score (positive = hot, negative =
    /// cold), magnitude from the two-tailed significance level — 3 at 99%
    /// (p &lt;= 0.01), 2 at 95% (p &lt;= 0.05), 1 at 90% (p &lt;= 0.10), 0 when
    /// not statistically significant.
    /// </summary>
    private static int ConfidenceBin(double zScore, double pValue)
    {
        var magnitude = pValue switch
        {
            <= 0.01 => 3,
            <= 0.05 => 2,
            <= 0.10 => 1,
            _ => 0,
        };

        if (magnitude == 0)
        {
            return 0;
        }

        return zScore >= 0 ? magnitude : -magnitude;
    }

    /// <summary>
    /// Complementary error function via the Numerical Recipes rational
    /// approximation (fractional error &lt; 1.2e-7 across the whole range),
    /// avoiding a dependency on a non-AOT math package for the normal CDF.
    /// </summary>
    private static double Erfc(double x)
    {
        var z = Math.Abs(x);
        var t = 1.0 / (1.0 + (0.5 * z));

        // Horner evaluation of the NR polynomial, innermost coefficient first.
        var poly = 0.17087277;
        poly = -0.82215223 + (t * poly);
        poly = 1.48851587 + (t * poly);
        poly = -1.13520398 + (t * poly);
        poly = 0.27886807 + (t * poly);
        poly = -0.18628806 + (t * poly);
        poly = 0.09678418 + (t * poly);
        poly = 0.37409196 + (t * poly);
        poly = 1.00002368 + (t * poly);

        var ans = t * Math.Exp((-z * z) - 1.26551223 + (t * poly));
        return x >= 0.0 ? ans : 2.0 - ans;
    }

    private static (double ValueSum, int Count) SumNeighbours(
        STRtree<int> index,
        List<MaterializedPoint> points,
        int origin,
        double distanceBand)
    {
        var originPoint = points[origin];
        var envelope = new Envelope(
            originPoint.X - distanceBand,
            originPoint.X + distanceBand,
            originPoint.Y - distanceBand,
            originPoint.Y + distanceBand);

        var bandSquared = distanceBand * distanceBand;
        double valueSum = 0;
        var count = 0;
        foreach (var candidate in index.Query(envelope))
        {
            var dx = points[candidate].X - originPoint.X;
            var dy = points[candidate].Y - originPoint.Y;
            if ((dx * dx) + (dy * dy) <= bandSquared)
            {
                valueSum += points[candidate].Value;
                count++;
            }
        }

        return (valueSum, count);
    }

    private static STRtree<int> BuildIndex(
        List<MaterializedPoint> points,
        double distanceBand,
        CancellationToken cancellationToken)
    {
        var index = new STRtree<int>();
        for (var i = 0; i < points.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var point = points[i];
            index.Insert(
                new Envelope(
                    point.X - distanceBand,
                    point.X + distanceBand,
                    point.Y - distanceBand,
                    point.Y + distanceBand),
                i);
        }

        index.Build();
        return index;
    }

    private static List<MaterializedPoint> MaterializePoints(FeatureCollection source, string field)
    {
        var materialized = new List<MaterializedPoint>(source.Count);
        foreach (var feature in source)
        {
            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            var coord = geometry is Point p ? p.Coordinate : geometry.Centroid.Coordinate;
            if (coord is null || double.IsNaN(coord.X) || double.IsNaN(coord.Y))
            {
                continue;
            }

            if (!TryReadNumeric(feature, field, out var value))
            {
                throw new TransformInputException(
                    $"feature is missing a numeric value for analysis field '{field}'");
            }

            materialized.Add(new MaterializedPoint(feature, coord.X, coord.Y, value));
        }

        return materialized;
    }

    private static Feature WithStatistics(IFeature source, double zScore, double pValue, int bin)
    {
        var merged = new AttributesTable();
        if (source.Attributes is not null)
        {
            foreach (var name in source.Attributes.GetNames())
            {
                if (string.Equals(name, ZScoreAttribute, StringComparison.Ordinal)
                    || string.Equals(name, PValueAttribute, StringComparison.Ordinal)
                    || string.Equals(name, BinAttribute, StringComparison.Ordinal))
                {
                    continue;
                }

                merged.Add(name, source.Attributes.GetOptionalValue(name));
            }
        }

        merged.Add(ZScoreAttribute, zScore);
        merged.Add(PValueAttribute, pValue);
        merged.Add(BinAttribute, bin);
        return new Feature(source.Geometry, merged);
    }

    private static double ReadDistanceBand(StepInputReader inputs)
    {
        if (!inputs.TryGet("distanceBand", out var raw) || string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)
            || value <= 0)
        {
            throw new TransformInputException(
                "'distanceBand' must be a finite positive number (CRS units)");
        }

        return value;
    }

    private static bool TryReadNumeric(IFeature feature, string field, out double value)
    {
        value = 0;
        var attributes = feature.Attributes;
        if (attributes is null || !attributes.Exists(field))
        {
            return false;
        }

        var raw = attributes.GetOptionalValue(field);
        switch (raw)
        {
            case null:
                return false;
            case double d:
                value = d;
                return true;
            case float f:
                value = f;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case short s:
                value = s;
                return true;
            case decimal m:
                value = (double)m;
                return true;
            default:
                return double.TryParse(
                    Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);
        }
    }

    private readonly record struct MaterializedPoint(IFeature SourceFeature, double X, double Y, double Value);
}

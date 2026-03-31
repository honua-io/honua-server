// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Services;

/// <summary>
/// Pure classification algorithms for dividing numeric data into classes.
/// </summary>
internal static class ClassificationAlgorithms
{
    /// <summary>
    /// Computes equal-interval break values between min and max.
    /// </summary>
    public static double[] EqualInterval(double min, double max, int classCount)
    {
        if (classCount < 2)
            return [max];

        var interval = (max - min) / classCount;
        var breaks = new double[classCount - 1];
        for (var i = 0; i < breaks.Length; i++)
            breaks[i] = min + interval * (i + 1);

        return breaks;
    }

    /// <summary>
    /// Computes quantile (percentile) break values from sorted data.
    /// </summary>
    public static double[] Quantile(ReadOnlySpan<double> sortedValues, int classCount)
    {
        if (sortedValues.Length == 0 || classCount < 2)
            return [];

        var breaks = new double[classCount - 1];
        for (var i = 1; i < classCount; i++)
        {
            var position = (double)i / classCount * (sortedValues.Length - 1);
            var lower = (int)Math.Floor(position);
            var upper = Math.Min(lower + 1, sortedValues.Length - 1);
            var fraction = position - lower;

            breaks[i - 1] = sortedValues[lower] + fraction * (sortedValues[upper] - sortedValues[lower]);
        }

        return breaks;
    }

    /// <summary>
    /// Computes natural breaks (Jenks) via dynamic programming.
    /// Falls back to EqualInterval if data is uniform or convergence fails.
    /// </summary>
    public static double[] NaturalBreaks(ReadOnlySpan<double> values, int classCount)
    {
        if (values.Length == 0 || classCount < 2)
            return [];

        // Sort a copy
        var sorted = values.ToArray();
        Array.Sort(sorted);

        // If uniform data, fall back to equal interval
        if (sorted[0] >= sorted[^1] - double.Epsilon)
            return EqualInterval(sorted[0], sorted[^1], classCount);

        var n = sorted.Length;
        var k = classCount;

        // Cap class count to data count
        if (k > n)
            k = n;
        if (k < 2)
            return [sorted[^1]];

        // DP matrices: variance (ssd) and class assignment
        var ssd = new double[n + 1, k + 1];
        var classIndex = new int[n + 1, k + 1];

        // Initialize
        for (var i = 0; i <= n; i++)
            for (var j = 0; j <= k; j++)
            {
                ssd[i, j] = double.MaxValue;
                classIndex[i, j] = 0;
            }

        ssd[0, 0] = 0;

        // Precompute cumulative sums for O(1) variance computation
        var cumSum = new double[n + 1];
        var cumSqSum = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            cumSum[i + 1] = cumSum[i] + sorted[i];
            cumSqSum[i + 1] = cumSqSum[i] + sorted[i] * sorted[i];
        }

        // Fill DP for single class (j=1)
        for (var i = 1; i <= n; i++)
        {
            var mean = cumSum[i] / i;
            ssd[i, 1] = cumSqSum[i] - cumSum[i] * mean;
        }

        // Fill DP for j=2..k
        for (var j = 2; j <= k; j++)
        {
            for (var i = j; i <= n; i++)
            {
                for (var m = j - 1; m < i; m++)
                {
                    var count = i - m;
                    var sum = cumSum[i] - cumSum[m];
                    var sqSum = cumSqSum[i] - cumSqSum[m];
                    var mean = sum / count;
                    var variance = sqSum - sum * mean;

                    var cost = ssd[m, j - 1] + variance;
                    if (cost < ssd[i, j])
                    {
                        ssd[i, j] = cost;
                        classIndex[i, j] = m;
                    }
                }
            }
        }

        // Traceback to find break indices
        var breakIndices = new int[k - 1];
        var idx = n;
        for (var j = k; j > 1; j--)
        {
            var ci = classIndex[idx, j];
            breakIndices[j - 2] = ci;
            idx = ci;
        }

        // Convert indices to break values (value at boundary)
        var breaks = new double[k - 1];
        for (var i = 0; i < breaks.Length; i++)
        {
            var bi = breakIndices[i];
            // Break is between sorted[bi-1] and sorted[bi]
            if (bi > 0 && bi < n)
                breaks[i] = (sorted[bi - 1] + sorted[bi]) / 2d;
            else
                breaks[i] = sorted[Math.Max(0, bi)];
        }

        return breaks;
    }
}

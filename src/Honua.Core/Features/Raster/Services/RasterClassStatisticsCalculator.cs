// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Services;

/// <summary>
/// Pure, allocation-light computation of a class statistical signature from the aligned
/// per-pixel band vectors of a training AOI. Accumulates the co-moment sums in a single pass so
/// memory stays O(bands^2) regardless of pixel count, then derives the per-band mean vector, the
/// band-by-band <b>sample</b> covariance matrix (divided by n-1, the maximum-likelihood
/// classifier convention), and per-band univariate summaries.
/// </summary>
public static class RasterClassStatisticsCalculator
{
    /// <summary>
    /// Computes the signature for a class from its band vectors. A class with zero pixels
    /// yields a zero-count signature with zero mean/covariance; a class with one pixel yields
    /// that pixel as the mean and a zero covariance matrix (sample covariance is undefined for
    /// n &lt; 2).
    /// </summary>
    /// <param name="classId">Caller-assigned class identifier echoed onto the signature.</param>
    /// <param name="name">Optional class name echoed onto the signature.</param>
    /// <param name="vectors">Aligned per-pixel band vectors read from the class AOI.</param>
    public static RasterClassSignature Compute(int classId, string? name, RasterBandVectorSet vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);

        var bands = vectors.Bands;
        var bandCount = bands.Length;
        var pixels = vectors.Pixels;
        var n = pixels.Count;

        var sum = new double[bandCount];
        var min = new double[bandCount];
        var max = new double[bandCount];
        // Cross-product sums of mean-centred deviations, accumulated with a running mean so the
        // covariance is numerically stable and needs only one pass (streaming co-moments).
        var comoment = new double[bandCount][];
        for (var i = 0; i < bandCount; i++)
        {
            comoment[i] = new double[bandCount];
            min[i] = double.PositiveInfinity;
            max[i] = double.NegativeInfinity;
        }

        var mean = new double[bandCount];
        var processed = 0L;
        foreach (var pixel in pixels)
        {
            processed++;
            var inv = 1.0 / processed;
            // Deviations from the running mean BEFORE it is updated (Welford / Bennett).
            for (var i = 0; i < bandCount; i++)
            {
                var vi = pixel[i];
                sum[i] += vi;
                if (vi < min[i]) min[i] = vi;
                if (vi > max[i]) max[i] = vi;
            }

            for (var i = 0; i < bandCount; i++)
            {
                var di = pixel[i] - mean[i];
                for (var j = i; j < bandCount; j++)
                {
                    var dj = pixel[j] - mean[j];
                    comoment[i][j] += di * dj * (processed - 1) * inv;
                }
            }

            for (var i = 0; i < bandCount; i++)
            {
                mean[i] += (pixel[i] - mean[i]) * inv;
            }
        }

        var covariance = new double[bandCount][];
        for (var i = 0; i < bandCount; i++)
        {
            covariance[i] = new double[bandCount];
        }

        if (n >= 2)
        {
            var denom = n - 1;
            for (var i = 0; i < bandCount; i++)
            {
                for (var j = i; j < bandCount; j++)
                {
                    var cov = comoment[i][j] / denom;
                    covariance[i][j] = cov;
                    covariance[j][i] = cov;
                }
            }
        }

        var summaries = new RasterClassBandSummary[bandCount];
        for (var i = 0; i < bandCount; i++)
        {
            var variance = covariance[i][i];
            summaries[i] = new RasterClassBandSummary
            {
                Band = bands[i],
                Min = n > 0 ? min[i] : 0,
                Max = n > 0 ? max[i] : 0,
                Mean = n > 0 ? mean[i] : 0,
                StandardDeviation = variance > 0 ? Math.Sqrt(variance) : 0,
            };
        }

        // Zero the mean vector when there were no pixels so an empty class reads honestly.
        if (n == 0)
        {
            Array.Clear(mean);
        }

        return new RasterClassSignature
        {
            ClassId = classId,
            Name = name,
            PixelCount = n,
            Bands = bands,
            Mean = mean,
            Covariance = covariance,
            BandSummaries = summaries,
        };
    }
}

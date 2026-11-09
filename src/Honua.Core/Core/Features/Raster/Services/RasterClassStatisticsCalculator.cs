// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Services;

public static class RasterClassStatisticsCalculator
{
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

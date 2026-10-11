// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;

namespace Honua.Protocols.GeoServices.ImageServer;

/// <summary>Maps stored histogram ranges to the bin-edge convention used by GeoServices.</summary>
internal static class ImageServerHistogramProjection
{
    public static BandHistogram Project(RasterHistogram histogram)
    {
        var min = histogram.Min;
        var max = histogram.Max;
        if (histogram.BinCount == 1 && histogram.Counts.Length == 1 && histogram.Counts[0] > 0 &&
            double.IsFinite(min) && min == max)
        {
            // PostGIS returns a populated zero-width bin for constant rasters. Esri defines
            // width=(max-min)/size and places integer samples at unit-width bin centers.
            // Keep that half-unit width when both reconstructed midpoints are still the
            // source value. A sub-unit center such as 1e-20 is smaller than an ULP at 0.5,
            // so a fixed half-unit collapses the edges onto -0.5 and 0.5 and clients read
            // a center of 0. Large magnitudes need a local ULP, because half a unit rounds
            // away. Reject centers, including the finite double limits, that have no
            // distinct finite edges around the same center.
            var center = min;
            if (!TryCenteredEdges(center, out min, out max))
            {
                throw new InvalidDataException("The constant histogram cannot be represented by finite bin edges.");
            }
        }

        return new BandHistogram
        {
            Size = histogram.BinCount,
            Min = min,
            Max = max,
            Counts = histogram.Counts,
        };
    }

    private static bool TryCenteredEdges(double center, out double min, out double max)
    {
        if (TryHalfWidth(center, 0.5d, out min, out max))
        {
            return true;
        }

        var ulp = Math.Abs(center) * Math.ScaleB(1d, -52);
        if (ulp > 0d && TryHalfWidth(center, ulp, out min, out max))
        {
            return true;
        }

        min = center;
        max = center;
        for (var step = 0; step < 32; step++)
        {
            var nextMin = Math.BitDecrement(min);
            var nextMax = Math.BitIncrement(max);
            if (nextMin == min && nextMax == max)
            {
                break;
            }

            min = nextMin;
            max = nextMax;
            if (EdgesPreserveCenter(min, max, center))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryHalfWidth(double center, double halfWidth, out double min, out double max)
    {
        min = center - halfWidth;
        max = center + halfWidth;
        return EdgesPreserveCenter(min, max, center);
    }

    private static bool EdgesPreserveCenter(double min, double max, double center)
    {
        // A half-unit at ±2^52 rounds one edge onto the center (ties to even) while
        // both midpoint formulas still equal the center. The sample would sit on the
        // exclusive end of the bin, so both edges must stay strictly outside it.
        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= center || max <= center)
        {
            return false;
        }

        // Split the average so values near the double range stay finite. Also require
        // the direct midpoint, which is what turned ±0.5 edges into a center of 0.
        if (min / 2d + max / 2d != center)
        {
            return false;
        }

        var sum = min + max;
        return double.IsFinite(sum) && sum / 2d == center;
    }
}

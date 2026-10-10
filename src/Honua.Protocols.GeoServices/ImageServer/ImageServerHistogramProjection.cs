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
            // Keep the original count and center; widen by an ULP for large floating values
            // where adding/subtracting half a unit would round back to that center.
            // At the finite double limits no centered interval is representable;
            // reject it rather than publish infinity or move the source center.
            var halfWidth = Math.Max(0.5, Math.Abs(min) * Math.ScaleB(1d, -52));
            min -= halfWidth;
            max += halfWidth;
            if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min)
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
}

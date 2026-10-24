// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>Rejects a cold statistics computation before an oversized dense mosaic is allocated.</summary>
public sealed class RasterStatisticsBudgetExceededException : Exception
{
    /// <summary>Creates a rejection for an invalid or over-budget dense grid.</summary>
    public RasterStatisticsBudgetExceededException(long maximumSampleSlots)
        : base($"The dense mosaic exceeds the statistics budget of {maximumSampleSlots} pixel-band slots or has an invalid grid.")
    {
        MaximumSampleSlots = maximumSampleSlots;
    }

    /// <summary>The configured dense statistics computation ceiling.</summary>
    public long MaximumSampleSlots { get; }
}

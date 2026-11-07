// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public sealed class RasterStatisticsBudgetExceededException : Exception
{
    public RasterStatisticsBudgetExceededException(long maximumSampleSlots)
        : base($"The dense mosaic exceeds the statistics budget of {maximumSampleSlots} pixel-band slots or has an invalid grid.")
    {
        MaximumSampleSlots = maximumSampleSlots;
    }

    public long MaximumSampleSlots { get; }
}

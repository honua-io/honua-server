// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public enum RasterStretchType
{
    MinMax = 0,

    StandardDeviation = 1,

    PercentClip = 2,
}

public readonly record struct RasterStretch
{
    public required RasterStretchType StretchType { get; init; }

    public double NumberOfStandardDeviations { get; init; } = 2.5;

    public double MinPercent { get; init; } = 0.25;

    public double MaxPercent { get; init; } = 0.25;

    public double[]? StatisticsMin { get; init; }

    public double[]? StatisticsMax { get; init; }

    public RasterStretch()
    {
    }
}

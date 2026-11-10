// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.SpatialAnalytics.Domain;

public enum DensityBinningMode
{
    HexGrid,

    SquareGrid,
}

public readonly record struct DensityQuery
{
    public required DensityBinningMode Mode { get; init; }

    public required double CellSizeMeters { get; init; }

    public string? WeightField { get; init; }

    public int MaxCells { get; init; }

    public int MaxInputFeatures { get; init; }

    public const double MinCellSizeMeters = 1d;

    public const double MaxCellSizeMeters = 1_000_000d;
}

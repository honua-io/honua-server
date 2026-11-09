// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct ProjectedPoint(double X, double Y);

public readonly record struct RasterPointGrid
{
    public required double OriginX { get; init; }

    public required double OriginY { get; init; }

    public required double CellWidth { get; init; }

    public required double CellHeight { get; init; }
}

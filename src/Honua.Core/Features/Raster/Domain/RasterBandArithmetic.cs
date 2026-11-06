// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

// The method selects a hardcoded formula, so this boundary never accepts caller-supplied SQL.
public enum RasterBandArithmeticMethod
{
    Ndvi = 0,

    Ndwi = 1,

    Savi = 2,
}

// There is no free-form expression; the method is the injection boundary.
public readonly record struct RasterBandArithmetic
{
    public required int VisibleBand { get; init; }

    public required int InfraredBand { get; init; }

    public required RasterBandArithmeticMethod Method { get; init; }

    public RasterBandArithmetic()
    {
    }
}

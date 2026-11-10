// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.CogParser;

public readonly record struct TilePixelLayout(
    int TileWidth,
    int SamplesPerPixel,
    int BitsPerSample,
    int Predictor,
    bool IsLittleEndian)
{
    public const int PredictorNone = 1;

    public const int PredictorHorizontalDifferencing = 2;

    public const int PredictorFloatingPoint = 3;

    public static TilePixelLayout None { get; } = new(0, 1, 8, PredictorNone, true);

    public bool HasPredictor => Predictor != PredictorNone;
}

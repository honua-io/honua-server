// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>
/// Pixel geometry of a single COG tile, supplying the context a TIFF predictor
/// needs to be reversed after decompression.
/// </summary>
/// <param name="TileWidth">Tile width in pixels (TIFF tag 322).</param>
/// <param name="SamplesPerPixel">Samples per pixel (TIFF tag 277); the predictor's sample stride.</param>
/// <param name="BitsPerSample">Bits per sample (TIFF tag 258).</param>
/// <param name="Predictor">TIFF predictor (tag 317): 1 = none, 2 = horizontal differencing.</param>
/// <param name="IsLittleEndian">Byte order of the source TIFF, which multi-byte samples are stored in.</param>
public readonly record struct TilePixelLayout(
    int TileWidth,
    int SamplesPerPixel,
    int BitsPerSample,
    int Predictor,
    bool IsLittleEndian)
{
    /// <summary>No predictor: decompressed bytes are the final pixel bytes.</summary>
    public const int PredictorNone = 1;

    /// <summary>Horizontal differencing predictor (TIFF tag 317 = 2).</summary>
    public const int PredictorHorizontalDifferencing = 2;

    /// <summary>Floating-point predictor (TIFF tag 317 = 3).</summary>
    public const int PredictorFloatingPoint = 3;

    /// <summary>
    /// Layout for tiles that carry no predictor, where pixel geometry is irrelevant to decoding.
    /// </summary>
    public static TilePixelLayout None { get; } = new(0, 1, 8, PredictorNone, true);

    /// <summary>
    /// Whether this layout requires a predictor pass after decompression.
    /// </summary>
    public bool HasPredictor => Predictor != PredictorNone;
}

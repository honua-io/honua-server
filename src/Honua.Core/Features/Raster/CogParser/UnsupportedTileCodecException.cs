// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>
/// Thrown when a COG tile uses a compression codec this reader cannot decode.
/// Names the offending codec and the codecs that are supported so callers and
/// operators can act on the failure without inspecting the file by hand.
/// </summary>
public sealed class UnsupportedTileCodecException : NotSupportedException
{
    /// <summary>
    /// The TIFF compression name that could not be decoded (for example <c>LERC</c>).
    /// </summary>
    public string Codec { get; }

    /// <summary>
    /// The TIFF compression names this reader can decode.
    /// </summary>
    public IReadOnlyList<string> SupportedCodecs { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedTileCodecException"/> class.
    /// </summary>
    /// <param name="codec">The unsupported TIFF compression name.</param>
    /// <param name="supportedCodecs">The TIFF compression names that are supported.</param>
    public UnsupportedTileCodecException(string codec, IReadOnlyList<string> supportedCodecs)
        : base($"COG tile compression '{codec}' is not supported. Supported codecs: {string.Join(", ", supportedCodecs)}. " +
               "Re-encode the source raster with a supported codec (for example gdal_translate -co COMPRESS=DEFLATE).")
    {
        Codec = codec;
        SupportedCodecs = supportedCodecs;
    }
}

/// <summary>
/// Thrown when a COG tile declares a TIFF predictor this reader cannot reverse.
/// Applying the wrong predictor silently produces plausible but incorrect pixels,
/// so an unrecognised predictor is surfaced rather than ignored.
/// </summary>
public sealed class UnsupportedTilePredictorException : NotSupportedException
{
    /// <summary>
    /// The TIFF predictor value (tag 317) that could not be reversed.
    /// </summary>
    public int Predictor { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedTilePredictorException"/> class.
    /// </summary>
    /// <param name="predictor">The unsupported TIFF predictor value.</param>
    /// <param name="detail">Detail describing why the predictor cannot be reversed.</param>
    public UnsupportedTilePredictorException(int predictor, string detail)
        : base($"COG tile predictor {predictor} (TIFF tag 317) is not supported: {detail}")
    {
        Predictor = predictor;
    }
}

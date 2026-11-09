// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.CogParser;

public sealed class UnsupportedTileCodecException : NotSupportedException
{
    public string Codec { get; }

    public IReadOnlyList<string> SupportedCodecs { get; }

    public UnsupportedTileCodecException(string codec, IReadOnlyList<string> supportedCodecs)
        : base($"COG tile compression '{codec}' is not supported. Supported codecs: {string.Join(", ", supportedCodecs)}. " +
               "Re-encode the source raster with a supported codec (for example gdal_translate -co COMPRESS=DEFLATE).")
    {
        Codec = codec;
        SupportedCodecs = supportedCodecs;
    }
}

// An unrecognized predictor is rejected, because applying the wrong one would yield plausible but wrong pixels.
public sealed class UnsupportedTilePredictorException : NotSupportedException
{
    public int Predictor { get; }

    public UnsupportedTilePredictorException(int predictor, string detail)
        : base($"COG tile predictor {predictor} (TIFF tag 317) is not supported: {detail}")
    {
        Predictor = predictor;
    }
}

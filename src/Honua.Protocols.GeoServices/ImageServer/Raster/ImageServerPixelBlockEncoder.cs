// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Services;

namespace Honua.Protocols.GeoServices.ImageServer.Raster;

/// <summary>Raw pixel-block layouts the exportImage <c>format</c> parameter can request.</summary>
internal enum ImageServerPixelBlockLayout
{
    /// <summary>Band-sequential samples followed by a packed pixel mask (<c>bsq</c>, SOAP <c>esriImageBSQ</c>).</summary>
    Bsq,

    /// <summary>Band-interleaved-by-pixel samples followed by a packed pixel mask (<c>bip</c>).</summary>
    Bip,

    /// <summary>One lossless LERC2 blob per band, concatenated in band order (<c>lerc</c>).</summary>
    Lerc,
}

/// <summary>
/// Frames canonical planar samples (<see cref="RasterFormat.Raw"/>) in the raw pixel-block layout
/// a caller requested from exportImage.
/// </summary>
internal static class ImageServerPixelBlockEncoder
{
    internal static bool TryParseLayout(string? format, out ImageServerPixelBlockLayout layout)
    {
        switch (format?.Trim().ToLowerInvariant())
        {
            case "bsq":
                layout = ImageServerPixelBlockLayout.Bsq;
                return true;
            case "bip":
                layout = ImageServerPixelBlockLayout.Bip;
                return true;
            case "lerc":
                layout = ImageServerPixelBlockLayout.Lerc;
                return true;
            default:
                layout = default;
                return false;
        }
    }

    internal static RasterResult Encode(RasterResult raster, ImageServerPixelBlockLayout layout, bool requireAllBandsValid = false)
    {
        if (requireAllBandsValid && layout != ImageServerPixelBlockLayout.Bsq)
            throw new InvalidOperationException("Stored MatchAny override is supported only for SOAP BSQ output.");
        return layout switch
        {
            ImageServerPixelBlockLayout.Bsq => ImageServerBsqEncoder.Encode(raster, requireAllBandsValid),
            ImageServerPixelBlockLayout.Bip => EncodeBip(raster),
            ImageServerPixelBlockLayout.Lerc => ImageServerLercEncoder.Encode(raster),
            _ => throw new ArgumentOutOfRangeException(nameof(layout)),
        };
    }

    // BIP carries the same samples and the same any-band pixel mask as BSQ; only the sample
    // order differs (every band of a pixel, then the next pixel).
    private static RasterResult EncodeBip(RasterResult raster)
    {
        var bsq = ImageServerBsqEncoder.Encode(raster);
        var bytesPerSample = ImageServerLercEncoder.BytesPerSample(raster.PixelType);
        var pixels = raster.Width * raster.Height;
        var bandLength = pixels * bytesPerSample;
        var sampleLength = bandLength * raster.BandCount;
        var data = new byte[bsq.Data.Length];
        var source = bsq.Data.AsSpan();
        var target = data.AsSpan();
        for (var band = 0; band < raster.BandCount; band++)
        {
            for (var pixel = 0; pixel < pixels; pixel++)
            {
                source.Slice((band * bandLength) + (pixel * bytesPerSample), bytesPerSample)
                    .CopyTo(target[(((pixel * raster.BandCount) + band) * bytesPerSample)..]);
            }
        }

        source[sampleLength..].CopyTo(target[sampleLength..]);
        return bsq with { Data = data };
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>Frames canonical planar samples as uncompressed Esri BSQ with a packed pixel mask.</summary>
internal static class ImageServerBsqEncoder
{
    internal static RasterResult Encode(RasterResult raster, bool requireAllBandsValid = false)
    {
        var bytesPerSample = raster.PixelType?.ToUpperInvariant() switch
        {
            "8BUI" or "8BSI" => 1,
            "16BUI" or "16BSI" => 2,
            "32BUI" or "32BSI" or "32BF" => 4,
            "64BF" => 8,
            _ => throw new NotSupportedException("Unsupported BSQ sample type.")
        };
        if (raster.Width <= 0 || raster.Height <= 0 || raster.BandCount <= 0 ||
            raster.ContentType != "application/octet-stream")
        {
            throw new InvalidDataException("Invalid canonical raw raster layout.");
        }
        var pixels = checked(raster.Width * raster.Height);
        var dataLength = checked((long)pixels * raster.BandCount * bytesPerSample);
        if (dataLength != raster.Data.Length) throw new InvalidDataException("Raw sample length does not match its layout.");
        var maskLength = (pixels + 7) / 8;
        var data = new byte[checked(raster.Data.Length + maskLength)];
        raster.Data.CopyTo(data, 0);
        var mask = data.AsSpan(raster.Data.Length);
        if (raster.BandValidityMasks is { } bandMasks)
        {
            if (bandMasks.Length != raster.BandCount || bandMasks.Any(band => band is null || band.Length != maskLength))
            {
                throw new InvalidDataException("Band validity masks do not match the raster layout.");
            }
            // Default MatchAll keeps a pixel when any band is valid. A SOAP override
            // already proven equal to stored per-band NoData may request MatchAny:
            // then every band must be valid. Never mutate the original sample bytes.
            if (requireAllBandsValid) mask.Fill(0xff);
            foreach (var band in bandMasks)
                for (var index = 0; index < maskLength; index++)
                    if (requireAllBandsValid) mask[index] &= band[index];
                    else mask[index] |= band[index];
        }
        else
        {
            mask.Fill(0xff);
        }
        if (pixels % 8 != 0) mask[^1] &= (byte)(0xff << (8 - pixels % 8));
        return raster with { Data = data, ContentType = "application/octet-stream" };
    }
}

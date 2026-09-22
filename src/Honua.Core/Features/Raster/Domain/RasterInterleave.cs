// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Sample-layout helpers for raw (container-less) raster exports.
/// </summary>
/// <remarks>
/// <see cref="RasterFormat.Raw"/> is produced through GDAL's <c>EHdr</c> driver, whose
/// primary file is the bare sample buffer in the raster's native type, laid out band
/// interleaved by line (BIL). Esri clients read image-service pixels as band sequential
/// (BSQ: every sample of band 1, then every sample of band 2, ...), which is what
/// <c>esriImageBSQ</c> names, so a multi-band buffer is re-interleaved here. For a single
/// band the two layouts are the same bytes.
/// </remarks>
public static class RasterInterleave
{
    /// <summary>
    /// Returns the number of bytes one sample occupies in a raw buffer for a PostGIS
    /// band pixel type (<c>8BUI</c>, <c>16BSI</c>, <c>32BF</c>, ...). Sub-byte types are
    /// written as whole bytes by the raw drivers.
    /// </summary>
    public static int BytesPerSample(string? pixelType)
        => pixelType?.Trim().ToUpperInvariant() switch
        {
            "1BB" or "2BUI" or "4BUI" or "8BSI" or "8BUI" => 1,
            "16BSI" or "16BUI" => 2,
            "32BSI" or "32BUI" or "32BF" => 4,
            "64BF" => 8,
            _ => throw new ArgumentException($"Unknown raster pixel type '{pixelType}'.", nameof(pixelType))
        };

    /// <summary>
    /// Re-lays a band-interleaved-by-line buffer out band sequentially. The input is
    /// returned unchanged when it holds a single band.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The buffer length does not equal <c>width * height * bands * bytesPerSample</c>.
    /// </exception>
    public static byte[] BandInterleavedByLineToBandSequential(
        byte[] data, int width, int height, int bands, int bytesPerSample)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bands);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSample);

        var expected = checked((long)width * height * bands * bytesPerSample);
        if (data.Length != expected)
        {
            throw new ArgumentException(
                $"Raw raster buffer holds {data.Length} bytes; {width}x{height}x{bands} samples of {bytesPerSample} byte(s) need {expected}.",
                nameof(data));
        }

        if (bands == 1)
        {
            return data;
        }

        var rowBytes = width * bytesPerSample;
        var bandBytes = rowBytes * height;
        var result = new byte[data.Length];
        for (var row = 0; row < height; row++)
        {
            for (var band = 0; band < bands; band++)
            {
                var source = (row * bands + band) * rowBytes;
                var target = band * bandBytes + row * rowBytes;
                Buffer.BlockCopy(data, source, result, target, rowBytes);
            }
        }

        return result;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Pre-processing admission control that bounds the DECODED PIXEL FOOTPRINT of an
/// inbound raster BEFORE any GDAL tool opens it and allocates a
/// width×height×bands×dtype buffer (#2766).
///
/// <para>
/// The worker already caps the base64/decoded BYTE size of every source
/// (<see cref="GdalWorkerOptions.MaxArtifactBytes"/>), but a few-KB, highly
/// compressible GeoTIFF can declare enormous <c>ImageWidth</c>/<c>ImageLength</c>
/// in its header and force GDAL to materialize an arbitrarily large raster —
/// a classic decompression bomb → OOM. This guard reads ONLY the TIFF header
/// (the IFD directory that sits near the front of the already-decoded, byte-
/// bounded blob — no pixel data, no subprocess, no GDAL dependency) to learn the
/// DECLARED dimensions and rejects the job up front when they exceed the
/// configured caps.
/// </para>
///
/// <para>
/// The check is deliberately conservative: if the payload is not a TIFF we can
/// parse (an unrecognized byte order/magic, a truncated header, or another GDAL
/// raster format), the guard ADMITS the job — GDAL opens the same header the same
/// way and either handles it or fails cheaply without a full allocation. The
/// guard's job is specifically to catch a parseable header that DECLARES an
/// oversized raster, which is the decompression-bomb vector.
/// </para>
/// </summary>
internal static class GdalRasterDimensionGuard
{
    /// <summary>Declared dimensions read from a raster header.</summary>
    internal readonly record struct RasterDimensions(long Width, long Height, int Bands, int BitsPerSample)
    {
        /// <summary>Total declared cell count (width × height).</summary>
        public long PixelCount => checked(Width * Height);

        /// <summary>
        /// Estimated fully-decoded byte footprint GDAL would allocate:
        /// width × height × bands × bytes-per-sample. This is the true OOM bound
        /// the decompression bomb targets.
        /// </summary>
        public long EstimatedDecodedBytes =>
            checked(Width * Height * Bands * ((BitsPerSample + 7) / 8));
    }

    /// <summary>
    /// Admits or rejects a decoded raster blob against the configured pixel-
    /// dimension caps. Returns <c>true</c> (admit) when the blob is within limits
    /// OR when its header cannot be parsed as a TIFF (see class remarks); returns
    /// <c>false</c> with a caller-facing <paramref name="error"/> when a parsed
    /// header declares dimensions over a cap.
    /// </summary>
    public static bool TryAdmit(ReadOnlySpan<byte> raster, GdalWorkerOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = "";

        if (!TryReadGeoTiffDimensions(raster, out var dims))
        {
            // Not a parseable TIFF header — nothing to bound here.
            return true;
        }

        return IsWithinLimits(dims, options, out error);
    }

    /// <summary>
    /// Admits or rejects a set of decoded raster blobs (e.g. the map-algebra /
    /// mosaic multi-source list). Any single source over a cap rejects the job,
    /// with the failing source index surfaced in the error.
    /// </summary>
    public static bool TryAdmitSources(IReadOnlyList<byte[]> sources, GdalWorkerOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(sources);
        error = "";
        for (var i = 0; i < sources.Count; i++)
        {
            if (!TryAdmit(sources[i], options, out var sourceError))
            {
                error = $"source #{(i + 1).ToString(CultureInfo.InvariantCulture)}: {sourceError}";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Pure evaluation of already-read <paramref name="dims"/> against the caps.
    /// Split out from header parsing so it is unit-testable without any raster
    /// bytes or a GDAL binary.
    /// </summary>
    public static bool IsWithinLimits(RasterDimensions dims, GdalWorkerOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = "";

        if (dims.Width > options.MaxRasterWidth)
        {
            error = $"declared raster width {dims.Width.ToString(CultureInfo.InvariantCulture)} exceeds configured MaxRasterWidth={options.MaxRasterWidth.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        if (dims.Height > options.MaxRasterHeight)
        {
            error = $"declared raster height {dims.Height.ToString(CultureInfo.InvariantCulture)} exceeds configured MaxRasterHeight={options.MaxRasterHeight.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        if (dims.Bands > options.MaxRasterBands)
        {
            error = $"declared raster band count {dims.Bands.ToString(CultureInfo.InvariantCulture)} exceeds configured MaxRasterBands={options.MaxRasterBands.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        // width×height and width×height×bands×dtype are computed with checked
        // arithmetic; a header declaring dimensions large enough to overflow Int64
        // is by definition past any sane cap, so treat the overflow as a rejection.
        long pixels;
        long decodedBytes;
        try
        {
            pixels = dims.PixelCount;
            decodedBytes = dims.EstimatedDecodedBytes;
        }
        catch (OverflowException)
        {
            error = "declared raster dimensions overflow the pixel-count bound";
            return false;
        }

        if (pixels > options.MaxRasterPixels)
        {
            error = $"declared raster pixel count {pixels.ToString(CultureInfo.InvariantCulture)} (width×height) exceeds configured MaxRasterPixels={options.MaxRasterPixels.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        if (decodedBytes > options.MaxDecodedRasterBytes)
        {
            error = $"estimated decoded raster size {decodedBytes.ToString(CultureInfo.InvariantCulture)} bytes (width×height×bands×dtype) exceeds configured MaxDecodedRasterBytes={options.MaxDecodedRasterBytes.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        return true;
    }

    // --- TIFF / BigTIFF header reader ---------------------------------------
    //
    // Reads ONLY the first Image File Directory to extract the tags that determine
    // the decoded footprint. Deliberately minimal: single-value ImageWidth (256),
    // ImageLength (257), SamplesPerPixel (277), and BitsPerSample (258). Any parse
    // difficulty returns false so the caller admits the job.

    private const int TagImageWidth = 256;
    private const int TagImageLength = 257;
    private const int TagBitsPerSample = 258;
    private const int TagSamplesPerPixel = 277;

    private const int TypeShort = 3;   // 16-bit unsigned
    private const int TypeLong = 4;    // 32-bit unsigned
    private const int TypeLong8 = 16;  // 64-bit unsigned (BigTIFF)

    internal static bool TryReadGeoTiffDimensions(ReadOnlySpan<byte> data, out RasterDimensions dims)
    {
        dims = default;
        if (data.Length < 8)
        {
            return false;
        }

        bool little;
        if (data[0] == 0x49 && data[1] == 0x49)
        {
            little = true; // "II"
        }
        else if (data[0] == 0x4D && data[1] == 0x4D)
        {
            little = false; // "MM"
        }
        else
        {
            return false;
        }

        try
        {
            var magic = ReadU16(data, 2, little);
            return magic switch
            {
                42 => TryReadClassic(data, little, out dims),
                43 => TryReadBigTiff(data, little, out dims),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            // Truncated / malformed header — admit and let GDAL adjudicate.
            return false;
        }
    }

    private static bool TryReadClassic(ReadOnlySpan<byte> data, bool little, out RasterDimensions dims)
    {
        dims = default;
        var ifdOffset = (long)ReadU32(data, 4, little);
        if (ifdOffset <= 0 || ifdOffset + 2 > data.Length)
        {
            return false;
        }

        int entryCount = ReadU16(data, (int)ifdOffset, little);
        var entriesStart = ifdOffset + 2;
        const int EntrySize = 12;
        if (entriesStart + (long)entryCount * EntrySize > data.Length)
        {
            return false;
        }

        long width = -1, height = -1, samples = 1, bits = 8;
        for (var i = 0; i < entryCount; i++)
        {
            var b = (int)(entriesStart + i * EntrySize);
            int tag = ReadU16(data, b, little);
            int type = ReadU16(data, b + 2, little);
            var valueFieldOffset = b + 8; // 4-byte value/offset field
            AssignTag(data, little, tag, type, valueFieldOffset, valueInlineWidth: 4,
                ref width, ref height, ref samples, ref bits);
        }

        return Finish(width, height, samples, bits, out dims);
    }

    private static bool TryReadBigTiff(ReadOnlySpan<byte> data, bool little, out RasterDimensions dims)
    {
        dims = default;
        // BigTIFF header: bytesize-of-offsets @4 (must be 8), 0 @6, first-IFD @8.
        int bytesizeOfOffsets = ReadU16(data, 4, little);
        if (bytesizeOfOffsets != 8)
        {
            return false;
        }

        var ifdOffset = (long)ReadU64(data, 8, little);
        if (ifdOffset <= 0 || ifdOffset + 8 > data.Length)
        {
            return false;
        }

        var entryCountRaw = ReadU64(data, (int)ifdOffset, little);
        if (entryCountRaw > 4096)
        {
            // An IFD with thousands of entries is not a raster we need to bound
            // here; bail to the admit path rather than scan an absurd directory.
            return false;
        }

        var entryCount = (int)entryCountRaw;
        var entriesStart = ifdOffset + 8;
        const int EntrySize = 20;
        if (entriesStart + (long)entryCount * EntrySize > data.Length)
        {
            return false;
        }

        long width = -1, height = -1, samples = 1, bits = 8;
        for (var i = 0; i < entryCount; i++)
        {
            var b = (int)(entriesStart + i * EntrySize);
            int tag = ReadU16(data, b, little);
            int type = ReadU16(data, b + 2, little);
            var valueFieldOffset = b + 12; // 8-byte value/offset field
            AssignTag(data, little, tag, type, valueFieldOffset, valueInlineWidth: 8,
                ref width, ref height, ref samples, ref bits);
        }

        return Finish(width, height, samples, bits, out dims);
    }

    private static void AssignTag(
        ReadOnlySpan<byte> data,
        bool little,
        int tag,
        int type,
        int valueFieldOffset,
        int valueInlineWidth,
        ref long width,
        ref long height,
        ref long samples,
        ref long bits)
    {
        switch (tag)
        {
            case TagImageWidth:
                if (TryReadScalar(data, little, type, valueFieldOffset, out var w))
                {
                    width = w;
                }
                break;
            case TagImageLength:
                if (TryReadScalar(data, little, type, valueFieldOffset, out var h))
                {
                    height = h;
                }
                break;
            case TagSamplesPerPixel:
                if (TryReadScalar(data, little, type, valueFieldOffset, out var s))
                {
                    samples = s;
                }
                break;
            case TagBitsPerSample:
                // BitsPerSample has one entry per sample. When it fits inline the
                // first value is at the value field; when it is stored out-of-line
                // the value field holds an offset to the SHORT array. Read the
                // first element either way — bands are effectively homogeneous.
                if (type == TypeShort)
                {
                    var arrayWidth = 2; // SHORT
                    var readAt = arrayWidth <= valueInlineWidth
                        ? valueFieldOffset
                        : (int)ReadOffset(data, little, valueFieldOffset, valueInlineWidth);
                    if (readAt >= 0 && readAt + 2 <= data.Length)
                    {
                        bits = ReadU16(data, readAt, little);
                    }
                }
                break;
        }
    }

    private static long ReadOffset(ReadOnlySpan<byte> data, bool little, int valueFieldOffset, int valueInlineWidth)
        => valueInlineWidth == 8
            ? (long)ReadU64(data, valueFieldOffset, little)
            : ReadU32(data, valueFieldOffset, little);

    private static bool TryReadScalar(ReadOnlySpan<byte> data, bool little, int type, int valueFieldOffset, out long value)
    {
        switch (type)
        {
            case TypeShort:
                value = ReadU16(data, valueFieldOffset, little);
                return true;
            case TypeLong:
                value = ReadU32(data, valueFieldOffset, little);
                return true;
            case TypeLong8:
                value = (long)ReadU64(data, valueFieldOffset, little);
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static bool Finish(long width, long height, long samples, long bits, out RasterDimensions dims)
    {
        dims = default;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        if (samples < 1)
        {
            samples = 1;
        }
        if (bits < 1)
        {
            bits = 8;
        }

        // Clamp band/bit counts into int range for the record; anything past that
        // is already absurd and the width/height alone will trip the caps.
        var bands = samples > int.MaxValue ? int.MaxValue : (int)samples;
        var bitsPerSample = bits > int.MaxValue ? int.MaxValue : (int)bits;
        dims = new RasterDimensions(width, height, bands, bitsPerSample);
        return true;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset, bool little)
    {
        var slice = data.Slice(offset, 2);
        return little
            ? BinaryPrimitives.ReadUInt16LittleEndian(slice)
            : BinaryPrimitives.ReadUInt16BigEndian(slice);
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool little)
    {
        var slice = data.Slice(offset, 4);
        return little
            ? BinaryPrimitives.ReadUInt32LittleEndian(slice)
            : BinaryPrimitives.ReadUInt32BigEndian(slice);
    }

    private static ulong ReadU64(ReadOnlySpan<byte> data, int offset, bool little)
    {
        var slice = data.Slice(offset, 8);
        return little
            ? BinaryPrimitives.ReadUInt64LittleEndian(slice)
            : BinaryPrimitives.ReadUInt64BigEndian(slice);
    }
}

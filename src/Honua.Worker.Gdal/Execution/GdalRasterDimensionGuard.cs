// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalRasterDimensionGuard
{
    internal readonly record struct RasterDimensions(long Width, long Height, int Bands, int BitsPerSample)
    {
        public long PixelCount => checked(Width * Height);

        public long EstimatedDecodedBytes =>
            checked(Width * Height * Bands * ((BitsPerSample + 7) / 8));
    }

    public static bool TryAdmit(ReadOnlySpan<byte> raster, GdalWorkerOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = "";

        // Positive raster-format allowlist. A payload whose magic identifies a
        // KNOWN raster container OUTSIDE the configured allowlist (e.g. JPEG2000 / GIF /
        // BMP / NITF / HFA) would sail past the dimension read below — the guard only
        // parses TIFF/PNG/JPEG headers — and then be opened UNBOUNDED by GDAL, a live
        // decompression-bomb → OOM vector. Refuse it here, before any subprocess spawns.
        var format = ClassifyContainer(raster);
        if (!TryAdmitContainerFormat(format, options, out error))
        {
            return false;
        }

        if (!TryReadRasterDimensions(raster, out var dims))
        {
            // Header not recognized as one of the compressible raster containers
            // we parse (TIFF/BigTIFF, PNG, JPEG) — nothing to bound cheaply here.
            return true;
        }

        return IsWithinLimits(dims, options, out error);
    }

    internal static bool TryAdmitContainerFormat(
        RasterContainerFormat format,
        GdalWorkerOptions options,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = "";

        if (format is RasterContainerFormat.Unknown)
        {
            error = "raster input format could not be identified and is not in the configured AllowedRasterInputFormats allowlist";
            return false;
        }

        if (IsFormatAllowed(format, options))
        {
            return true;
        }

        error = $"raster input format '{FormatName(format)}' is not in the configured AllowedRasterInputFormats allowlist";
        return false;
    }

    internal enum RasterContainerFormat
    {
        Unknown = 0,

        Tiff,

        Png,

        Jpeg,

        Jpeg2000,

        Gif,

        Bmp,

        Nitf,

        Hfa,
    }

    internal static RasterContainerFormat ClassifyContainer(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return RasterContainerFormat.Unknown;
        }

        // TIFF / BigTIFF: "II" or "MM" byte-order mark.
        if ((data[0] == 0x49 && data[1] == 0x49) || (data[0] == 0x4D && data[1] == 0x4D))
        {
            return RasterContainerFormat.Tiff;
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A.
        if (data.Length >= 8
            && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return RasterContainerFormat.Png;
        }

        // JPEG 2000 raw codestream: FF 4F FF 51 (SOC + SIZ). Checked before JPEG so the
        // FF-lead does not collide.
        if (data[0] == 0xFF && data[1] == 0x4F && data[2] == 0xFF && data[3] == 0x51)
        {
            return RasterContainerFormat.Jpeg2000;
        }

        // JPEG: SOI = FF D8.
        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            return RasterContainerFormat.Jpeg;
        }

        // JP2 box format: 00 00 00 0C 6A 50 20 20 0D 0A 87 0A (signature "jP  " box).
        if (data.Length >= 12
            && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x0C
            && data[4] == 0x6A && data[5] == 0x50 && data[6] == 0x20 && data[7] == 0x20
            && data[8] == 0x0D && data[9] == 0x0A && data[10] == 0x87 && data[11] == 0x0A)
        {
            return RasterContainerFormat.Jpeg2000;
        }

        // GIF: "GIF8" (87a / 89a share this 4-byte lead).
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
        {
            return RasterContainerFormat.Gif;
        }

        // BMP / DIB: "BM".
        if (data[0] == 0x42 && data[1] == 0x4D)
        {
            return RasterContainerFormat.Bmp;
        }

        // NITF: "NITF".
        if (data[0] == 0x4E && data[1] == 0x49 && data[2] == 0x54 && data[3] == 0x46)
        {
            return RasterContainerFormat.Nitf;
        }

        // Erdas Imagine HFA: files begin with the ASCII "EHFA_HEADER_TAG".
        if (data[0] == 0x45 && data[1] == 0x48 && data[2] == 0x46 && data[3] == 0x41)
        {
            return RasterContainerFormat.Hfa;
        }

        return RasterContainerFormat.Unknown;
    }

    private static string FormatName(RasterContainerFormat format) => format switch
    {
        RasterContainerFormat.Tiff => "TIFF",
        RasterContainerFormat.Png => "PNG",
        RasterContainerFormat.Jpeg => "JPEG",
        RasterContainerFormat.Jpeg2000 => "JPEG2000",
        RasterContainerFormat.Gif => "GIF",
        RasterContainerFormat.Bmp => "BMP",
        RasterContainerFormat.Nitf => "NITF",
        RasterContainerFormat.Hfa => "HFA",
        _ => "UNKNOWN",
    };

    private static bool IsFormatAllowed(RasterContainerFormat format, GdalWorkerOptions options)
    {
        var name = FormatName(format);
        return options.AllowedRasterInputFormats.Any(allowed =>
            !string.IsNullOrWhiteSpace(allowed)
            && string.Equals(allowed.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryReadRasterDimensions(ReadOnlySpan<byte> data, out RasterDimensions dims)
    {
        dims = default;
        return ClassifyContainer(data) switch
        {
            RasterContainerFormat.Tiff => TryReadGeoTiffDimensions(data, out dims),
            RasterContainerFormat.Png => TryReadPngDimensions(data, out dims),
            RasterContainerFormat.Jpeg => TryReadJpegDimensions(data, out dims),
            _ => false,
        };
    }

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

    public static bool IsWithinLimits(RasterDimensions dims, GdalWorkerOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = "";

        if (dims.Width <= 0 || dims.Height <= 0 || dims.Bands <= 0 || dims.BitsPerSample <= 0)
        {
            error = "declared raster dimensions must all be positive";
            return false;
        }

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
            long count = ReadU32(data, b + 4, little);
            var valueFieldOffset = b + 8; // 4-byte value/offset field
            AssignTag(data, little, tag, type, count, valueFieldOffset, valueInlineWidth: 4,
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
            var countRaw = ReadU64(data, b + 4, little);
            long count = countRaw > long.MaxValue ? long.MaxValue : (long)countRaw;
            var valueFieldOffset = b + 12; // 8-byte value/offset field
            AssignTag(data, little, tag, type, count, valueFieldOffset, valueInlineWidth: 8,
                ref width, ref height, ref samples, ref bits);
        }

        return Finish(width, height, samples, bits, out dims);
    }

    private static void AssignTag(
        ReadOnlySpan<byte> data,
        bool little,
        int tag,
        int type,
        long count,
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
                // BitsPerSample stores count SHORT entries (count * 2 bytes). When the full
                // array exceeds valueInlineWidth, the field holds a file offset to the array.
                if (type == TypeShort)
                {
                    var sampleCount = count > 0 ? count : 1L;
                    var fitsInline = sampleCount <= valueInlineWidth / 2;
                    long readAtLong = fitsInline
                        ? valueFieldOffset
                        : ReadOffset(data, little, valueFieldOffset, valueInlineWidth);
                    if (readAtLong >= 0 && readAtLong + 2 <= data.Length)
                    {
                        var readAt = (int)readAtLong;
                        var toRead = (int)Math.Min(sampleCount, Math.Min(512L, (data.Length - readAt) / 2L));
                        long maxBits = 0;
                        for (var idx = 0; idx < toRead; idx++)
                        {
                            var sampleBits = ReadU16(data, readAt + idx * 2, little);
                            if (sampleBits > maxBits)
                            {
                                maxBits = sampleBits;
                            }
                        }

                        if (maxBits > 0)
                        {
                            bits = maxBits;
                        }
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
                // A LONG8 dimension past Int64.Max would wrap negative on a naive
                // cast and then be silently ADMITTED by Finish's width<=0 guard.
                // Clamp to Int64.Max (fail closed): a positive, absurdly large
                // value the caps then reject.
                var raw = ReadU64(data, valueFieldOffset, little);
                value = raw > long.MaxValue ? long.MaxValue : (long)raw;
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

    // --- PNG header reader -----------------------------------------------------
    //
    // The IHDR chunk is fixed at the front of every PNG: 8-byte signature, then a
    // 4-byte length, the "IHDR" tag, then width(4, BE) + height(4, BE) +
    // bitDepth(1) + colourType(1). A few-KB PNG can declare a 100000×100000 canvas
    // exactly like a TIFF bomb, so it must be bounded the same way.

    private static bool TryReadPngDimensions(ReadOnlySpan<byte> data, out RasterDimensions dims)
    {
        dims = default;
        // 8 (sig) + 4 (len) + 4 ("IHDR") + 13 (IHDR data) = 33 bytes minimum.
        if (data.Length < 33)
        {
            return false;
        }

        if (data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R')
        {
            return false;
        }

        long width = ReadU32(data, 16, little: false);
        long height = ReadU32(data, 20, little: false);
        int bitDepth = data[24];
        int colourType = data[25];

        // Samples per pixel by PNG colour type. Indexed (3) stores one sample but
        // GDAL expands the palette to RGB(A); count it as 4 so the byte estimate
        // does not under-bound the post-expansion allocation.
        var samples = colourType switch
        {
            0 => 1, // greyscale
            2 => 3, // truecolour RGB
            3 => 4, // indexed → palette-expanded to RGBA by GDAL
            4 => 2, // greyscale + alpha
            6 => 4, // RGBA
            _ => 4, // unknown → conservative
        };

        return Finish(width, height, samples, bitDepth, out dims);
    }

    // --- JPEG header reader ----------------------------------------------------
    //
    // Scan the marker segments for a Start-Of-Frame (SOFn) marker, which carries
    // precision, height(2, BE), width(2, BE), components(1). JPEG dimensions max at
    // 65535 each, but 65535×65535×3 ≈ 12.8 GB decoded — well past the caps — so a
    // JPEG bomb is real and must be bounded.

    private static bool TryReadJpegDimensions(ReadOnlySpan<byte> data, out RasterDimensions dims)
    {
        dims = default;
        var pos = 2; // past SOI (FF D8)
        // Bound the scan so a pathological marker stream cannot spin.
        for (var guard = 0; guard < 4096; guard++)
        {
            if (pos + 4 > data.Length || data[pos] != 0xFF)
            {
                return false;
            }

            var marker = data[pos + 1];

            // Standalone markers (no length): RSTn (D0–D7), SOI (D8), EOI (D9),
            // TEM (01), and padding FF bytes.
            if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                pos += 2;
                continue;
            }
            if (marker == 0xFF)
            {
                pos += 1; // fill byte
                continue;
            }

            var segLength = ReadU16(data, pos + 2, little: false);
            if (segLength < 2)
            {
                return false;
            }

            // SOF0/1/2/3, 5/6/7, 9/10/11, 13/14/15 are frame headers; C4 (DHT),
            // C8 (JPG), CC (DAC) are NOT frame headers and are skipped.
            var isSof = (marker >= 0xC0 && marker <= 0xCF)
                && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof)
            {
                // Segment: [len(2)][precision(1)][height(2)][width(2)][components(1)]
                // components byte is at index pos+9, so we need pos+10 <= length.
                if (pos + 10 > data.Length)
                {
                    return false;
                }
                int precision = data[pos + 4];
                long height = ReadU16(data, pos + 5, little: false);
                long width = ReadU16(data, pos + 7, little: false);
                int components = data[pos + 9];
                return Finish(width, height, components, precision, out dims);
            }

            pos += 2 + segLength;
        }

        return false;
    }

    // --- GeoTIFF ModelPixelScale reader ---------------------------------------
    //
    // Reads tag 33550 (ModelPixelScaleTag: scaleX, scaleY[, scaleZ] in CRS ground
    // units per pixel) so the resample -tr output bound can derive the input's ground
    // extent (declared dimensions × pixel scale). The three DOUBLEs are stored
    // out-of-line (24 bytes > any inline value field), so the entry's value field holds
    // an offset to the array. Any parse difficulty leaves the identity default in place.

    private const int TagModelPixelScale = 33550;
    private const int TagModelTiepoint = 33922;
    private const int TagModelTransformation = 34264;
    private const int TagGeoKeyDirectory = 34735;
    private const int TypeDouble = 12; // 64-bit IEEE double

    private const ushort GeoKeyModelType = 1024;
    private const ushort GeoKeyGeographicType = 2048;
    private const ushort GeoKeyProjectedCsType = 3072;
    private const ushort GeoKeyModelTypeProjected = 1;
    private const ushort GeoKeyUserDefined = 32767;

    internal readonly record struct RasterGeoBounds(
        double MinX,
        double MinY,
        double MaxX,
        double MaxY,
        double ScaleX,
        double ScaleY,
        int? EpsgCode);

    private readonly record struct TiffIfdLayout(
        bool LittleEndian,
        long EntriesStart,
        int EntryCount,
        int EntrySize,
        bool IsBigTiff);

    private struct GeoTiffTagOffsets
    {
        public long? ScaleOffset;
        public long? TiepointOffset;
        public long? TransformOffset;
        public long? GeoKeyOffset;
        public int GeoKeyCount;
    }

    internal static bool TryReadGeoTiffBounds(
        ReadOnlySpan<byte> data,
        RasterDimensions dims,
        out RasterGeoBounds bounds)
    {
        bounds = default;
        if (!TryLocateFirstIfd(data, out var layout))
        {
            return false;
        }

        try
        {
            var tags = ScanGeoTiffTags(data, layout);
            var epsgCode = ReadGeoKeyEpsg(data, layout.LittleEndian, tags);
            return TryBuildBoundsFromScaleAndTiepoint(data, layout.LittleEndian, dims, tags, epsgCode, out bounds)
                || TryBuildBoundsFromTransformation(data, layout.LittleEndian, dims, tags, epsgCode, out bounds);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryLocateFirstIfd(ReadOnlySpan<byte> data, out TiffIfdLayout layout)
    {
        layout = default;
        if (ClassifyContainer(data) is not RasterContainerFormat.Tiff || data.Length < 8)
        {
            return false;
        }

        var little = data[0] == 0x49;
        var magic = ReadU16(data, 2, little);
        return magic switch
        {
            42 => TryLocateClassicIfd(data, little, out layout),
            43 => TryLocateBigTiffIfd(data, little, out layout),
            _ => false,
        };
    }

    private static bool TryLocateClassicIfd(ReadOnlySpan<byte> data, bool little, out TiffIfdLayout layout)
    {
        layout = default;
        var ifdOffset = (long)ReadU32(data, 4, little);
        if (ifdOffset <= 0 || ifdOffset + 2 > data.Length)
        {
            return false;
        }

        int entryCount = ReadU16(data, (int)ifdOffset, little);
        var entriesStart = ifdOffset + 2;
        const int EntrySize = 12;
        if (entriesStart + ((long)entryCount * EntrySize) > data.Length)
        {
            return false;
        }

        layout = new TiffIfdLayout(little, entriesStart, entryCount, EntrySize, IsBigTiff: false);
        return true;
    }

    private static bool TryLocateBigTiffIfd(ReadOnlySpan<byte> data, bool little, out TiffIfdLayout layout)
    {
        layout = default;
        if (data.Length < 16 || ReadU16(data, 4, little) != 8)
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
            return false;
        }

        var entryCount = (int)entryCountRaw;
        var entriesStart = ifdOffset + 8;
        const int EntrySize = 20;
        if (entriesStart + ((long)entryCount * EntrySize) > data.Length)
        {
            return false;
        }

        layout = new TiffIfdLayout(little, entriesStart, entryCount, EntrySize, IsBigTiff: true);
        return true;
    }

    private static GeoTiffTagOffsets ScanGeoTiffTags(ReadOnlySpan<byte> data, TiffIfdLayout layout)
    {
        var offsets = new GeoTiffTagOffsets();
        var valueDelta = layout.IsBigTiff ? 12 : 8;
        for (var i = 0; i < layout.EntryCount; i++)
        {
            var pos = (int)(layout.EntriesStart + ((long)i * layout.EntrySize));
            var tag = ReadU16(data, pos, layout.LittleEndian);
            var type = ReadU16(data, pos + 2, layout.LittleEndian);
            var count = layout.IsBigTiff
                ? ReadU64(data, pos + 4, layout.LittleEndian)
                : ReadU32(data, pos + 4, layout.LittleEndian);
            var offset = layout.IsBigTiff
                ? (long)ReadU64(data, pos + valueDelta, layout.LittleEndian)
                : ReadU32(data, pos + valueDelta, layout.LittleEndian);
            RecordGeoTiffTag(ref offsets, tag, type, count, offset);
        }

        return offsets;
    }

    private static void RecordGeoTiffTag(
        ref GeoTiffTagOffsets offsets,
        int tag,
        int type,
        ulong count,
        long offset)
    {
        if (tag == TagModelPixelScale && type == TypeDouble && count >= 2)
        {
            offsets.ScaleOffset = offset;
        }
        else if (tag == TagModelTiepoint && type == TypeDouble && count >= 6)
        {
            offsets.TiepointOffset = offset;
        }
        else if (tag == TagModelTransformation && type == TypeDouble && count >= 16)
        {
            offsets.TransformOffset = offset;
        }
        else if (tag == TagGeoKeyDirectory && type == TypeShort && count >= 4)
        {
            offsets.GeoKeyOffset = offset;
            offsets.GeoKeyCount = (int)Math.Min(count, 4096UL);
        }
    }

    private static int? ReadGeoKeyEpsg(ReadOnlySpan<byte> data, bool little, GeoTiffTagOffsets tags)
    {
        if (!tags.GeoKeyOffset.HasValue || tags.GeoKeyCount < 4)
        {
            return null;
        }

        var start = tags.GeoKeyOffset.Value;
        var byteLength = (long)tags.GeoKeyCount * 2;
        if (start < 0 || start + byteLength > data.Length)
        {
            return null;
        }

        var keyCount = ReadU16(data, (int)start + 6, little);
        var maxKeys = Math.Min(keyCount, (tags.GeoKeyCount - 4) / 4);
        return ExtractEpsgFromGeoKeys(data, (int)start + 8, maxKeys, little);
    }

    private static int? ExtractEpsgFromGeoKeys(ReadOnlySpan<byte> data, int entriesOffset, int keyCount, bool little)
    {
        ushort modelType = 0;
        int? projectedEpsg = null;
        int? geographicEpsg = null;
        var userDefinedProjected = false;

        for (var i = 0; i < keyCount; i++)
        {
            var entry = entriesOffset + (i * 8);
            var tiffTagLocation = ReadU16(data, entry + 2, little);
            if (tiffTagLocation != 0)
            {
                continue;
            }

            var keyId = ReadU16(data, entry, little);
            var valueOffset = ReadU16(data, entry + 6, little);
            ApplyGeoKey(keyId, valueOffset, ref modelType, ref projectedEpsg, ref geographicEpsg, ref userDefinedProjected);
        }

        if (projectedEpsg.HasValue)
        {
            return projectedEpsg.Value;
        }

        return userDefinedProjected || modelType == GeoKeyModelTypeProjected ? null : geographicEpsg;
    }

    private static void ApplyGeoKey(
        ushort keyId,
        ushort valueOffset,
        ref ushort modelType,
        ref int? projectedEpsg,
        ref int? geographicEpsg,
        ref bool userDefinedProjected)
    {
        if (keyId == GeoKeyModelType)
        {
            modelType = valueOffset;
        }
        else if (keyId == GeoKeyProjectedCsType)
        {
            userDefinedProjected |= valueOffset == GeoKeyUserDefined;
            if (valueOffset > 0 && valueOffset != GeoKeyUserDefined)
            {
                projectedEpsg = valueOffset;
            }
        }
        else if (keyId == GeoKeyGeographicType && valueOffset > 0 && valueOffset != GeoKeyUserDefined)
        {
            geographicEpsg = valueOffset;
        }
    }

    private static bool TryBuildBoundsFromScaleAndTiepoint(
        ReadOnlySpan<byte> data,
        bool little,
        RasterDimensions dims,
        GeoTiffTagOffsets tags,
        int? epsgCode,
        out RasterGeoBounds bounds)
    {
        bounds = default;
        if (!tags.ScaleOffset.HasValue || !tags.TiepointOffset.HasValue)
        {
            return false;
        }

        var scaleOffset = tags.ScaleOffset.Value;
        var tieOffset = tags.TiepointOffset.Value;
        if (scaleOffset < 0 || scaleOffset + 16 > data.Length || tieOffset < 0 || tieOffset + 48 > data.Length)
        {
            return false;
        }

        var sx = ReadDouble(data, (int)scaleOffset, little);
        var sy = ReadDouble(data, (int)scaleOffset + 8, little);
        var i = ReadDouble(data, (int)tieOffset, little);
        var j = ReadDouble(data, (int)tieOffset + 8, little);
        var x = ReadDouble(data, (int)tieOffset + 24, little);
        var y = ReadDouble(data, (int)tieOffset + 32, little);

        if (!double.IsFinite(sx) || sx <= 0d || !double.IsFinite(sy) || sy <= 0d
            || !double.IsFinite(i) || !double.IsFinite(j) || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return false;
        }

        var minX = x - (i * sx);
        var maxX = minX + (dims.Width * sx);
        var maxY = y + (j * sy);
        var minY = maxY - (dims.Height * sy);
        if (!double.IsFinite(minX) || !double.IsFinite(maxX) || !double.IsFinite(minY) || !double.IsFinite(maxY))
        {
            return false;
        }

        bounds = new RasterGeoBounds(minX, minY, maxX, maxY, sx, sy, epsgCode);
        return true;
    }

    private static bool TryBuildBoundsFromTransformation(
        ReadOnlySpan<byte> data,
        bool little,
        RasterDimensions dims,
        GeoTiffTagOffsets tags,
        int? epsgCode,
        out RasterGeoBounds bounds)
    {
        bounds = default;
        if (!tags.TransformOffset.HasValue)
        {
            return false;
        }

        var offset = tags.TransformOffset.Value;
        if (offset < 0 || offset + (16 * 8) > data.Length)
        {
            return false;
        }

        var m0 = ReadDouble(data, (int)offset, little);
        var m1 = ReadDouble(data, (int)offset + (1 * 8), little);
        var m3 = ReadDouble(data, (int)offset + (3 * 8), little);
        var m4 = ReadDouble(data, (int)offset + (4 * 8), little);
        var m5 = ReadDouble(data, (int)offset + (5 * 8), little);
        var m7 = ReadDouble(data, (int)offset + (7 * 8), little);

        var sx = double.Hypot(m0, m4);
        var sy = double.Hypot(m1, m5);
        if (!double.IsFinite(sx) || sx <= 0d || !double.IsFinite(sy) || sy <= 0d)
        {
            return false;
        }

        var xs = new[] { m3, (m0 * dims.Width) + m3, (m1 * dims.Height) + m3, (m0 * dims.Width) + (m1 * dims.Height) + m3 };
        var ys = new[] { m7, (m4 * dims.Width) + m7, (m5 * dims.Height) + m7, (m4 * dims.Width) + (m5 * dims.Height) + m7 };
        var minX = xs.Min();
        var maxX = xs.Max();
        var minY = ys.Min();
        var maxY = ys.Max();
        if (!double.IsFinite(minX) || !double.IsFinite(maxX) || !double.IsFinite(minY) || !double.IsFinite(maxY))
        {
            return false;
        }

        bounds = new RasterGeoBounds(minX, minY, maxX, maxY, sx, sy, epsgCode);
        return true;
    }

    internal static void ReadGeoTiffPixelScale(ReadOnlySpan<byte> data, out double scaleX, out double scaleY)
    {
        scaleX = 1.0;
        scaleY = 1.0;

        if (ClassifyContainer(data) is not RasterContainerFormat.Tiff || data.Length < 8)
        {
            return;
        }

        var little = data[0] == 0x49; // "II" little-endian, else "MM" big-endian
        try
        {
            var magic = ReadU16(data, 2, little);
            switch (magic)
            {
                case 42:
                    ReadClassicPixelScale(data, little, ref scaleX, ref scaleY);
                    break;
                case 43:
                    ReadBigTiffPixelScale(data, little, ref scaleX, ref scaleY);
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            // Truncated / malformed header — keep the identity default; the dimension
            // guard adjudicates the same bytes and GDAL opens them the same way.
            scaleX = 1.0;
            scaleY = 1.0;
        }
    }

    private static void ReadClassicPixelScale(ReadOnlySpan<byte> data, bool little, ref double scaleX, ref double scaleY)
    {
        var ifdOffset = (long)ReadU32(data, 4, little);
        if (ifdOffset <= 0 || ifdOffset + 2 > data.Length)
        {
            return;
        }

        int entryCount = ReadU16(data, (int)ifdOffset, little);
        var entriesStart = ifdOffset + 2;
        const int EntrySize = 12;
        if (entriesStart + (long)entryCount * EntrySize > data.Length)
        {
            return;
        }

        long? transformOffset = null;
        for (var i = 0; i < entryCount; i++)
        {
            var b = (int)(entriesStart + i * EntrySize);
            var tag = ReadU16(data, b, little);
            if (tag == TagModelPixelScale)
            {
                if (ReadU16(data, b + 2, little) != TypeDouble || ReadU32(data, b + 4, little) < 2)
                {
                    return;
                }
                // 4-byte value field holds the offset to the out-of-line DOUBLE array.
                var arrayOffset = (long)ReadU32(data, b + 8, little);
                ReadScalePair(data, little, arrayOffset, ref scaleX, ref scaleY);
                return;
            }

            if (tag == TagModelTransformation &&
                ReadU16(data, b + 2, little) == TypeDouble &&
                ReadU32(data, b + 4, little) >= 16)
            {
                transformOffset = (long)ReadU32(data, b + 8, little);
            }
        }

        if (transformOffset.HasValue)
        {
            ReadModelTransformationScale(data, little, transformOffset.Value, ref scaleX, ref scaleY);
        }
    }

    private static void ReadBigTiffPixelScale(ReadOnlySpan<byte> data, bool little, ref double scaleX, ref double scaleY)
    {
        if (ReadU16(data, 4, little) != 8)
        {
            return;
        }

        var ifdOffset = (long)ReadU64(data, 8, little);
        if (ifdOffset <= 0 || ifdOffset + 8 > data.Length)
        {
            return;
        }

        var entryCountRaw = ReadU64(data, (int)ifdOffset, little);
        if (entryCountRaw > 4096)
        {
            return;
        }

        var entryCount = (int)entryCountRaw;
        var entriesStart = ifdOffset + 8;
        const int EntrySize = 20;
        if (entriesStart + (long)entryCount * EntrySize > data.Length)
        {
            return;
        }

        long? transformOffset = null;
        for (var i = 0; i < entryCount; i++)
        {
            var b = (int)(entriesStart + i * EntrySize);
            var tag = ReadU16(data, b, little);
            if (tag == TagModelPixelScale)
            {
                if (ReadU16(data, b + 2, little) != TypeDouble || ReadU64(data, b + 4, little) < 2)
                {
                    return;
                }
                // 8-byte value field holds the offset to the out-of-line DOUBLE array.
                var arrayOffset = (long)ReadU64(data, b + 12, little);
                ReadScalePair(data, little, arrayOffset, ref scaleX, ref scaleY);
                return;
            }

            if (tag == TagModelTransformation &&
                ReadU16(data, b + 2, little) == TypeDouble &&
                ReadU64(data, b + 4, little) >= 16)
            {
                transformOffset = (long)ReadU64(data, b + 12, little);
            }
        }

        if (transformOffset.HasValue)
        {
            ReadModelTransformationScale(data, little, transformOffset.Value, ref scaleX, ref scaleY);
        }
    }

    private static void ReadModelTransformationScale(ReadOnlySpan<byte> data, bool little, long arrayOffset, ref double scaleX, ref double scaleY)
    {
        if (arrayOffset < 0 || arrayOffset + (16 * 8) > data.Length)
        {
            return;
        }

        var m0 = ReadDouble(data, (int)arrayOffset, little);
        var m1 = ReadDouble(data, (int)arrayOffset + (1 * 8), little);
        var m4 = ReadDouble(data, (int)arrayOffset + (4 * 8), little);
        var m5 = ReadDouble(data, (int)arrayOffset + (5 * 8), little);

        var sx = Math.Sqrt((m0 * m0) + (m4 * m4));
        var sy = Math.Sqrt((m1 * m1) + (m5 * m5));
        if (double.IsFinite(sx) && sx > 0d && double.IsFinite(sy) && sy > 0d)
        {
            scaleX = sx;
            scaleY = sy;
        }
    }

    private static void ReadScalePair(ReadOnlySpan<byte> data, bool little, long arrayOffset, ref double scaleX, ref double scaleY)
    {
        if (arrayOffset < 0 || arrayOffset + 16 > data.Length)
        {
            return;
        }

        var sx = ReadDouble(data, (int)arrayOffset, little);
        var sy = ReadDouble(data, (int)arrayOffset + 8, little);
        if (double.IsFinite(sx) && sx > 0d)
        {
            scaleX = sx;
        }
        if (double.IsFinite(sy) && sy > 0d)
        {
            scaleY = sy;
        }
    }

    private static double ReadDouble(ReadOnlySpan<byte> data, int offset, bool little)
    {
        var slice = data.Slice(offset, 8);
        return little
            ? BinaryPrimitives.ReadDoubleLittleEndian(slice)
            : BinaryPrimitives.ReadDoubleBigEndian(slice);
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

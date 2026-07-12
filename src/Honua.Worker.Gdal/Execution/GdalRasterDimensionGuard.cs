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

        // Positive raster-format allowlist (#2784). A payload whose magic identifies a
        // KNOWN raster container OUTSIDE the configured allowlist (e.g. JPEG2000 / GIF /
        // BMP / NITF / HFA) would sail past the dimension read below — the guard only
        // parses TIFF/PNG/JPEG headers — and then be opened UNBOUNDED by GDAL, a live
        // decompression-bomb → OOM vector. Refuse it here, before any subprocess spawns.
        var format = ClassifyContainer(raster);
        if (format is not RasterContainerFormat.Unknown && !IsFormatAllowed(format, options))
        {
            error = $"raster input format '{FormatName(format)}' is not in the configured AllowedRasterInputFormats allowlist";
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

    /// <summary>
    /// A raster container the worker can identify from its leading magic bytes.
    /// <see cref="Tiff"/> / <see cref="Png"/> / <see cref="Jpeg"/> are the
    /// dimension-guarded (allowlisted-by-default) formats; the remainder are KNOWN
    /// raster containers the guard cannot bound and that the positive allowlist refuses
    /// by default (#2784). <see cref="Unknown"/> covers anything else (vector payloads,
    /// truncated blobs) and is left for GDAL / the vector paths to adjudicate.
    /// </summary>
    internal enum RasterContainerFormat
    {
        /// <summary>Magic not identified as a known raster container.</summary>
        Unknown = 0,

        /// <summary>TIFF / BigTIFF (also the container for GeoTIFF and COG).</summary>
        Tiff,

        /// <summary>PNG.</summary>
        Png,

        /// <summary>JPEG (JFIF/EXIF baseline or progressive).</summary>
        Jpeg,

        /// <summary>JPEG 2000 codestream or JP2 box format.</summary>
        Jpeg2000,

        /// <summary>GIF (87a / 89a).</summary>
        Gif,

        /// <summary>Windows BMP / DIB.</summary>
        Bmp,

        /// <summary>NITF (National Imagery Transmission Format).</summary>
        Nitf,

        /// <summary>Erdas Imagine HFA (<c>.img</c>).</summary>
        Hfa,
    }

    /// <summary>
    /// Classifies the leading magic bytes into a <see cref="RasterContainerFormat"/>.
    /// Deliberately covers the dimension-guarded formats PLUS the known un-bounded
    /// raster bomb vectors named in #2784; anything else is <see cref="RasterContainerFormat.Unknown"/>.
    /// </summary>
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

    /// <summary>Canonical allowlist key for a classified format.</summary>
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
        foreach (var allowed in options.AllowedRasterInputFormats)
        {
            if (!string.IsNullOrWhiteSpace(allowed)
                && string.Equals(allowed.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads declared dimensions from any of the compressible raster container
    /// formats the worker ingests and that can be tiny-on-disk yet declare an
    /// enormous canvas: TIFF / BigTIFF, PNG, and JPEG. Dispatches by the leading
    /// magic bytes. Returns <c>false</c> for anything else (the caller then
    /// admits and lets GDAL adjudicate).
    /// </summary>
    internal static bool TryReadRasterDimensions(ReadOnlySpan<byte> data, out RasterDimensions dims)
    {
        dims = default;
        if (data.Length < 8)
        {
            return false;
        }

        // TIFF / BigTIFF: "II" or "MM".
        if ((data[0] == 0x49 && data[1] == 0x49) || (data[0] == 0x4D && data[1] == 0x4D))
        {
            return TryReadGeoTiffDimensions(data, out dims);
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A.
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return TryReadPngDimensions(data, out dims);
        }

        // JPEG: SOI = FF D8.
        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            return TryReadJpegDimensions(data, out dims);
        }

        return false;
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

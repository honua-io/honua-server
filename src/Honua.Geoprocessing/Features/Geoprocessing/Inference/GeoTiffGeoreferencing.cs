// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;

namespace Honua.Geoprocessing.Inference;

/// <summary>
/// Minimal, dependency-free GeoTIFF header reader used to PROVE that a delegated
/// inference output is actually georeferenced (#2241). TIFF magic alone is not
/// evidence: a plain unreferenced TIFF, or a truncated header, also starts with
/// <c>II*\0</c>. Publishing such a payload as a "GeoTIFF" would silently place a
/// classification at the wrong location while the process advertises
/// georeferencing preservation, so the executor parses the IFD and requires real
/// positioning + CRS metadata before the artifact is published.
/// </summary>
/// <remarks>
/// Reads only what the contract needs — image size (tags 256/257), the GeoTIFF
/// model tags (33550 ModelPixelScale, 33922 ModelTiepoint, 34264
/// ModelTransformation) and the GeoKeyDirectory (34735) CRS keys — so it stays a
/// pure span parse with no GDAL/native dependency and no allocation beyond the
/// parsed values. Both classic TIFF and BigTIFF, little- and big-endian, are
/// handled. Anything malformed simply fails to parse, which the caller surfaces
/// as a clear job failure.
/// </remarks>
internal readonly record struct GeoTiffGeoreferencing
{
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagModelPixelScale = 33550;
    private const ushort TagModelTiepoint = 33922;
    private const ushort TagModelTransformation = 34264;
    private const ushort TagGeoKeyDirectory = 34735;

    private const ushort GeoKeyGeographicType = 2048;
    private const ushort GeoKeyProjectedCsType = 3072;

    /// <summary>Raster width in pixels.</summary>
    public double Width { get; init; }

    /// <summary>Raster height in pixels.</summary>
    public double Height { get; init; }

    /// <summary>Georeferenced X of the upper-left corner.</summary>
    public double OriginX { get; init; }

    /// <summary>Georeferenced Y of the upper-left corner.</summary>
    public double OriginY { get; init; }

    /// <summary>Georeferenced pixel size along X (always positive).</summary>
    public double PixelSizeX { get; init; }

    /// <summary>Georeferenced pixel size along Y (always positive).</summary>
    public double PixelSizeY { get; init; }

    /// <summary>
    /// CRS code from the GeoKeyDirectory (ProjectedCSTypeGeoKey, else
    /// GeographicTypeGeoKey), or 0 when the directory declares neither.
    /// </summary>
    public int CrsCode { get; init; }

    /// <summary>Georeferenced extent width (<see cref="Width"/> * <see cref="PixelSizeX"/>).</summary>
    public double ExtentWidth => Width * PixelSizeX;

    /// <summary>Georeferenced extent height (<see cref="Height"/> * <see cref="PixelSizeY"/>).</summary>
    public double ExtentHeight => Height * PixelSizeY;

    /// <summary>
    /// True when the header carries both a usable model transform (origin +
    /// non-degenerate pixel size) and a CRS declaration.
    /// </summary>
    public bool IsGeoreferenced =>
        Width > 0 && Height > 0
        && PixelSizeX > 0 && PixelSizeY > 0
        && double.IsFinite(OriginX) && double.IsFinite(OriginY)
        && CrsCode != 0;

    /// <summary>
    /// Attempts to read the georeferencing block from a TIFF/BigTIFF payload.
    /// Returns false for non-TIFF, truncated, or unparseable input.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out GeoTiffGeoreferencing georeferencing)
    {
        georeferencing = default;

        if (bytes.Length < 8)
        {
            return false;
        }

        bool littleEndian;
        if (bytes[0] == 0x49 && bytes[1] == 0x49)
        {
            littleEndian = true;
        }
        else if (bytes[0] == 0x4D && bytes[1] == 0x4D)
        {
            littleEndian = false;
        }
        else
        {
            return false;
        }

        var version = ReadUInt16(bytes, 2, littleEndian);
        return version switch
        {
            42 => TryReadClassic(bytes, littleEndian, out georeferencing),
            43 => TryReadBigTiff(bytes, littleEndian, out georeferencing),
            _ => false
        };
    }

    private static bool TryReadClassic(
        ReadOnlySpan<byte> bytes,
        bool littleEndian,
        out GeoTiffGeoreferencing georeferencing)
    {
        georeferencing = default;

        var ifdOffset = ReadUInt32(bytes, 4, littleEndian);
        if (ifdOffset + 2 > (ulong)bytes.Length)
        {
            return false;
        }

        var entryCount = ReadUInt16(bytes, (int)ifdOffset, littleEndian);
        var entriesStart = (long)ifdOffset + 2;
        if (entriesStart + ((long)entryCount * 12) > bytes.Length)
        {
            return false;
        }

        var builder = new Builder();
        for (var i = 0; i < entryCount; i++)
        {
            var entry = (int)(entriesStart + (i * 12));
            var tag = ReadUInt16(bytes, entry, littleEndian);
            var type = ReadUInt16(bytes, entry + 2, littleEndian);
            var count = ReadUInt32(bytes, entry + 4, littleEndian);
            var valueFieldOffset = entry + 8;

            var elementSize = ElementSize(type);
            if (elementSize == 0)
            {
                continue;
            }

            var payloadBytes = count * (ulong)elementSize;
            int dataOffset;
            if (payloadBytes <= 4)
            {
                dataOffset = valueFieldOffset;
            }
            else
            {
                var pointer = ReadUInt32(bytes, valueFieldOffset, littleEndian);
                if (pointer + payloadBytes > (ulong)bytes.Length)
                {
                    continue;
                }

                dataOffset = (int)pointer;
            }

            builder.Accept(bytes, tag, type, count, dataOffset, littleEndian);
        }

        return builder.TryBuild(out georeferencing);
    }

    private static bool TryReadBigTiff(
        ReadOnlySpan<byte> bytes,
        bool littleEndian,
        out GeoTiffGeoreferencing georeferencing)
    {
        georeferencing = default;

        if (bytes.Length < 16 || ReadUInt16(bytes, 4, littleEndian) != 8)
        {
            return false;
        }

        var ifdOffset = ReadUInt64(bytes, 8, littleEndian);
        if (ifdOffset + 8 > (ulong)bytes.Length)
        {
            return false;
        }

        var entryCount = ReadUInt64(bytes, (int)ifdOffset, littleEndian);
        var entriesStart = (long)ifdOffset + 8;
        if (entryCount > 4096 || entriesStart + ((long)entryCount * 20) > bytes.Length)
        {
            return false;
        }

        var builder = new Builder();
        for (var i = 0UL; i < entryCount; i++)
        {
            var entry = (int)(entriesStart + ((long)i * 20));
            var tag = ReadUInt16(bytes, entry, littleEndian);
            var type = ReadUInt16(bytes, entry + 2, littleEndian);
            var count = ReadUInt64(bytes, entry + 4, littleEndian);
            var valueFieldOffset = entry + 12;

            var elementSize = ElementSize(type);
            if (elementSize == 0)
            {
                continue;
            }

            var payloadBytes = count * (ulong)elementSize;
            int dataOffset;
            if (payloadBytes <= 8)
            {
                dataOffset = valueFieldOffset;
            }
            else
            {
                var pointer = ReadUInt64(bytes, valueFieldOffset, littleEndian);
                if (pointer + payloadBytes > (ulong)bytes.Length)
                {
                    continue;
                }

                dataOffset = (int)pointer;
            }

            builder.Accept(bytes, tag, type, (uint)Math.Min(count, uint.MaxValue), dataOffset, littleEndian);
        }

        return builder.TryBuild(out georeferencing);
    }

    /// <summary>
    /// Compares this georeferencing block against <paramref name="source"/> and
    /// reports the first material mismatch, or null when the two agree. A
    /// backend may legitimately resample (different pixel size / raster size), so
    /// only the CRS and the covered extent are enforced — those are what place the
    /// classification on the map.
    /// </summary>
    public string? DescribeMismatchAgainst(GeoTiffGeoreferencing source)
    {
        if (source.CrsCode != 0 && CrsCode != source.CrsCode)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"CRS code {CrsCode} does not match the source CRS code {source.CrsCode}");
        }

        // Tolerance: one source pixel on each axis, floored at a small relative
        // epsilon so large projected coordinates do not trip on float noise.
        var toleranceX = Math.Max(source.PixelSizeX, Math.Abs(source.ExtentWidth) * 1e-6);
        var toleranceY = Math.Max(source.PixelSizeY, Math.Abs(source.ExtentHeight) * 1e-6);

        if (Math.Abs(OriginX - source.OriginX) > toleranceX
            || Math.Abs(OriginY - source.OriginY) > toleranceY)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"origin ({OriginX}, {OriginY}) does not match the source origin ({source.OriginX}, {source.OriginY})");
        }

        if (Math.Abs(ExtentWidth - source.ExtentWidth) > toleranceX
            || Math.Abs(ExtentHeight - source.ExtentHeight) > toleranceY)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"extent {ExtentWidth} x {ExtentHeight} does not match the source extent {source.ExtentWidth} x {source.ExtentHeight}");
        }

        return null;
    }

    private static int ElementSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => 0
    };

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..])
            : BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..])
            : BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..])
            : BinaryPrimitives.ReadUInt64BigEndian(bytes[offset..]);

    private static double ReadDouble(ReadOnlySpan<byte> bytes, int offset, bool littleEndian)
        => BitConverter.Int64BitsToDouble(
            (long)(littleEndian
                ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..])
                : BinaryPrimitives.ReadUInt64BigEndian(bytes[offset..])));

    /// <summary>Accumulates the tags of interest while the IFD is walked.</summary>
    private struct Builder
    {
        private double _width;
        private double _height;
        private double _scaleX;
        private double _scaleY;
        private double _tiepointX;
        private double _tiepointY;
        private bool _hasTiepoint;
        private bool _hasScale;
        private double _matrixOriginX;
        private double _matrixOriginY;
        private double _matrixScaleX;
        private double _matrixScaleY;
        private bool _hasMatrix;
        private int _crsCode;

        public void Accept(
            ReadOnlySpan<byte> bytes,
            ushort tag,
            ushort type,
            uint count,
            int dataOffset,
            bool littleEndian)
        {
            switch (tag)
            {
                case TagImageWidth:
                    _width = ReadScalar(bytes, type, dataOffset, littleEndian);
                    break;
                case TagImageLength:
                    _height = ReadScalar(bytes, type, dataOffset, littleEndian);
                    break;
                case TagModelPixelScale when count >= 2 && dataOffset + 16 <= bytes.Length:
                    _scaleX = Math.Abs(ReadDouble(bytes, dataOffset, littleEndian));
                    _scaleY = Math.Abs(ReadDouble(bytes, dataOffset + 8, littleEndian));
                    _hasScale = true;
                    break;
                case TagModelTiepoint when count >= 6 && dataOffset + 48 <= bytes.Length:
                    // (i, j, k, x, y, z) — the raster point and its model point.
                    _tiepointX = ReadDouble(bytes, dataOffset + 24, littleEndian);
                    _tiepointY = ReadDouble(bytes, dataOffset + 32, littleEndian);
                    _hasTiepoint = true;
                    break;
                case TagModelTransformation when count >= 16 && dataOffset + 128 <= bytes.Length:
                    _matrixScaleX = Math.Abs(ReadDouble(bytes, dataOffset, littleEndian));
                    _matrixScaleY = Math.Abs(ReadDouble(bytes, dataOffset + 40, littleEndian));
                    _matrixOriginX = ReadDouble(bytes, dataOffset + 24, littleEndian);
                    _matrixOriginY = ReadDouble(bytes, dataOffset + 56, littleEndian);
                    _hasMatrix = true;
                    break;
                case TagGeoKeyDirectory:
                    _crsCode = ReadCrsCode(bytes, count, dataOffset, littleEndian);
                    break;
                default:
                    break;
            }
        }

        public bool TryBuild(out GeoTiffGeoreferencing georeferencing)
        {
            double originX;
            double originY;
            double pixelX;
            double pixelY;

            if (_hasScale && _hasTiepoint)
            {
                originX = _tiepointX;
                originY = _tiepointY;
                pixelX = _scaleX;
                pixelY = _scaleY;
            }
            else if (_hasMatrix)
            {
                originX = _matrixOriginX;
                originY = _matrixOriginY;
                pixelX = _matrixScaleX;
                pixelY = _matrixScaleY;
            }
            else
            {
                georeferencing = new GeoTiffGeoreferencing
                {
                    Width = _width,
                    Height = _height,
                    CrsCode = _crsCode
                };
                return true;
            }

            georeferencing = new GeoTiffGeoreferencing
            {
                Width = _width,
                Height = _height,
                OriginX = originX,
                OriginY = originY,
                PixelSizeX = pixelX,
                PixelSizeY = pixelY,
                CrsCode = _crsCode
            };
            return true;
        }

        private static int ReadCrsCode(
            ReadOnlySpan<byte> bytes,
            uint count,
            int dataOffset,
            bool littleEndian)
        {
            // GeoKeyDirectory: 4 header shorts then 4 shorts per key
            // (keyId, tiffTagLocation, count, valueOffset). An in-line key
            // (tiffTagLocation == 0) stores its value directly in valueOffset.
            if (count < 4 || dataOffset + (count * 2) > bytes.Length)
            {
                return 0;
            }

            var keyCount = ReadUInt16(bytes, dataOffset + 6, littleEndian);
            var geographic = 0;
            for (var k = 0; k < keyCount; k++)
            {
                var keyOffset = dataOffset + 8 + (k * 8);
                if (keyOffset + 8 > bytes.Length || (uint)(8 + (k * 8) + 8) > count * 2)
                {
                    break;
                }

                var keyId = ReadUInt16(bytes, keyOffset, littleEndian);
                var location = ReadUInt16(bytes, keyOffset + 2, littleEndian);
                var value = ReadUInt16(bytes, keyOffset + 6, littleEndian);
                if (location != 0)
                {
                    continue;
                }

                if (keyId == GeoKeyProjectedCsType && value != 0 && value != 32767)
                {
                    return value;
                }

                if (keyId == GeoKeyGeographicType && value != 0 && value != 32767)
                {
                    geographic = value;
                }
            }

            return geographic;
        }

        private static double ReadScalar(
            ReadOnlySpan<byte> bytes,
            ushort type,
            int dataOffset,
            bool littleEndian)
            => type switch
            {
                3 when dataOffset + 2 <= bytes.Length => ReadUInt16(bytes, dataOffset, littleEndian),
                4 when dataOffset + 4 <= bytes.Length => ReadUInt32(bytes, dataOffset, littleEndian),
                16 when dataOffset + 8 <= bytes.Length => ReadUInt64(bytes, dataOffset, littleEndian),
                _ => 0d
            };
    }
}

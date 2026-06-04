// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.PointCloud;

/// <summary>
/// Pure-managed reader for ASPRS LAS point-cloud files (#1201). Parses the LAS
/// public header block and streams point records, decoding position
/// (descaled to the file's CRS units), intensity, classification, and — for
/// colour-bearing point data record formats — per-point RGB.
/// </summary>
/// <remarks>
/// <para>
/// Supports the uncompressed LAS 1.1-1.4 point data record formats 0, 1, 2, 3,
/// 6, 7, and 8 (the formats produced by the overwhelming majority of LiDAR and
/// drone-photogrammetry exporters). Compressed LAZ and Cloud-Optimized Point
/// Cloud (COPC) inputs are detected and rejected with a clear error so callers
/// can surface a problem-detail rather than mis-parsing chunked data; native
/// LAZ/COPC decompression is a tracked follow-up.
/// </para>
/// <para>
/// The reader is deterministic and allocation-conscious: it reads point records
/// sequentially in file order, applies the header scale/offset, and yields a
/// <see cref="LasPoint"/> per record without buffering the whole cloud. Position
/// is returned in the file's stored horizontal units; the tiling pipeline is
/// responsible for any datum/CRS interpretation.
/// </para>
/// </remarks>
public static class LasPointCloudReader
{
    private const int PublicHeaderMinLength = 227; // LAS 1.2 public header size.

    /// <summary>
    /// Reads the LAS public header block from <paramref name="source"/> and
    /// validates that the file is a supported, uncompressed LAS.
    /// </summary>
    /// <exception cref="LasFormatException">The stream is not a supported LAS file.</exception>
    public static LasHeader ReadHeader(ReadOnlySpan<byte> source)
    {
        if (source.Length < PublicHeaderMinLength)
        {
            throw new LasFormatException(
                SceneGenerationErrorCodes.ModelAssetInvalid,
                "LAS file is shorter than the minimum public header block.");
        }

        if (source[0] != (byte)'L' || source[1] != (byte)'A' || source[2] != (byte)'S' || source[3] != (byte)'F')
        {
            throw new LasFormatException(
                SceneGenerationErrorCodes.ModelAssetInvalid,
                "LAS signature 'LASF' not found; the file is not a LAS point cloud.");
        }

        var versionMajor = source[24];
        var versionMinor = source[25];

        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(94, 2));
        var offsetToPointData = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(96, 4));
        var rawPointFormat = source[104];

        // Bit 7 (0x80) of the point data record format byte is the LAZ
        // compression flag set by laszip. We do not decode the arithmetic-coded
        // chunk table, so reject compressed inputs explicitly.
        var isCompressed = (rawPointFormat & 0x80) != 0 || (rawPointFormat & 0x40) != 0;
        if (isCompressed)
        {
            throw new LasFormatException(
                SceneGenerationErrorCodes.ModelAssetInvalid,
                "LAZ-compressed point clouds are not yet supported; supply an uncompressed LAS file.");
        }

        var pointFormat = (byte)(rawPointFormat & 0x3F);
        if (!IsSupportedFormat(pointFormat))
        {
            throw new LasFormatException(
                SceneGenerationErrorCodes.UnsupportedGeometryType,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "LAS point data record format {0} is not supported.",
                    pointFormat));
        }

        var pointRecordLength = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(105, 2));

        // Legacy 32-bit point count (LAS <=1.3). LAS 1.4 also carries a 64-bit
        // count at offset 247 which supersedes the legacy field when non-zero.
        ulong pointCount = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(107, 4));
        if (versionMajor == 1 && versionMinor >= 4 && source.Length >= 255)
        {
            var extended = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(247, 8));
            if (extended != 0)
            {
                pointCount = extended;
            }
        }

        var scaleX = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(131, 8));
        var scaleY = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(139, 8));
        var scaleZ = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(147, 8));
        var offsetX = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(155, 8));
        var offsetY = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(163, 8));
        var offsetZ = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(171, 8));
        var maxX = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(179, 8));
        var minX = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(187, 8));
        var maxY = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(195, 8));
        var minY = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(203, 8));
        var maxZ = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(211, 8));
        var minZ = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(219, 8));

        if (pointRecordLength < MinRecordLength(pointFormat))
        {
            throw new LasFormatException(
                SceneGenerationErrorCodes.ModelAssetInvalid,
                "LAS point record length is smaller than the declared point format requires.");
        }

        return new LasHeader
        {
            VersionMajor = versionMajor,
            VersionMinor = versionMinor,
            PointDataRecordFormat = pointFormat,
            PointRecordLength = pointRecordLength,
            OffsetToPointData = offsetToPointData,
            PointCount = pointCount,
            HeaderSize = headerSize,
            ScaleX = scaleX,
            ScaleY = scaleY,
            ScaleZ = scaleZ,
            OffsetXValue = offsetX,
            OffsetYValue = offsetY,
            OffsetZValue = offsetZ,
            MinX = minX,
            MinY = minY,
            MinZ = minZ,
            MaxX = maxX,
            MaxY = maxY,
            MaxZ = maxZ
        };
    }

    /// <summary>
    /// Streams the decoded points from a LAS buffer in file order. The header is
    /// parsed first; each yielded <see cref="LasPoint"/> carries descaled
    /// position plus the preserved intensity, classification, and (when present)
    /// RGB attributes.
    /// </summary>
    public static IEnumerable<LasPoint> ReadPoints(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var header = ReadHeader(source);
        return ReadPoints(source, header);
    }

    /// <summary>
    /// Streams the decoded points using a pre-parsed <paramref name="header"/>,
    /// avoiding a second header parse when the caller already has it.
    /// </summary>
    public static IEnumerable<LasPoint> ReadPoints(byte[] source, LasHeader header)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(header);

        var recordLength = header.PointRecordLength;
        var format = header.PointDataRecordFormat;
        var hasColor = FormatHasColor(format);
        var hasIntensity = true; // every supported format carries intensity.
        var colorOffset = ColorByteOffset(format);
        var classificationOffset = ClassificationByteOffset(format);

        var start = checked((int)header.OffsetToPointData);
        for (ulong i = 0; i < header.PointCount; i++)
        {
            var recordStart = start + checked((int)(i * (ulong)recordLength));
            if (recordStart + recordLength > source.Length)
            {
                throw new LasFormatException(
                    SceneGenerationErrorCodes.ModelAssetInvalid,
                    "LAS point data is truncated relative to the declared point count.");
            }

            var record = source.AsSpan(recordStart, recordLength);

            var xi = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(0, 4));
            var yi = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(4, 4));
            var zi = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(8, 4));

            var x = xi * header.ScaleX + header.OffsetXValue;
            var y = yi * header.ScaleY + header.OffsetYValue;
            var z = zi * header.ScaleZ + header.OffsetZValue;

            var intensity = hasIntensity
                ? BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(12, 2))
                : (ushort)0;

            var classification = record[classificationOffset];

            ushort r = 0, g = 0, b = 0;
            if (hasColor)
            {
                r = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(colorOffset, 2));
                g = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(colorOffset + 2, 2));
                b = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(colorOffset + 4, 2));
            }

            yield return new LasPoint(x, y, z, intensity, classification, hasColor, r, g, b);
        }
    }

    private static bool IsSupportedFormat(byte format) => format is 0 or 1 or 2 or 3 or 6 or 7 or 8;

    private static bool FormatHasColor(byte format) => format is 2 or 3 or 7 or 8;

    /// <summary>
    /// Minimum record length for each supported point data record format.
    /// </summary>
    private static int MinRecordLength(byte format) => format switch
    {
        0 => 20,
        1 => 28,
        2 => 26,
        3 => 34,
        6 => 30,
        7 => 36,
        8 => 38,
        _ => int.MaxValue
    };

    /// <summary>
    /// Byte offset of the classification field within a point record. Formats
    /// 0-5 pack it at offset 15; the extended formats 6-10 move it to offset 16.
    /// </summary>
    private static int ClassificationByteOffset(byte format) => format >= 6 ? 16 : 15;

    /// <summary>
    /// Byte offset of the first RGB channel within a colour-bearing record.
    /// </summary>
    private static int ColorByteOffset(byte format) => format switch
    {
        2 => 20, // base record (20 bytes) + RGB.
        3 => 28, // base + GPS time (8) + RGB.
        7 => 30, // extended base (30 bytes) + RGB.
        8 => 30, // extended base + RGB (+ NIR after).
        _ => -1
    };
}

/// <summary>
/// Raised when a LAS buffer is malformed or uses an unsupported format. Carries
/// a stable <see cref="SceneGenerationErrorCodes"/> code so the ingest executor
/// can surface a problem-detail without leaking parser internals.
/// </summary>
public sealed class LasFormatException : Exception
{
    /// <summary>Creates a <see cref="LasFormatException"/>.</summary>
    /// <param name="code">Stable scene error code (see <see cref="SceneGenerationErrorCodes"/>).</param>
    /// <param name="message">Human-readable, non-sensitive description.</param>
    public LasFormatException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Stable error code forwarded through problem-detail helpers.</summary>
    public string Code { get; }
}

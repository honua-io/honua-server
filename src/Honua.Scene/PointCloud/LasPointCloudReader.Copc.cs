// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.PointCloud;

public static partial class LasPointCloudReader
{
    private const ushort Las14MinHeaderSize = 375;
    private const int VlrHeaderLength = 54;
    private const int CopcInfoMinPayloadLength = 160;
    private const ushort CopcInfoRecordId = 1;

    private static ReadOnlySpan<byte> CopcUserId => "copc"u8;

    public static bool IsCopc(ReadOnlySpan<byte> source)
        => TryReadCopcInfo(source, out _, out _);

    public static CopcInfo ReadCopcInfo(ReadOnlySpan<byte> source)
    {
        if (!TryReadCopcInfo(source, out var info, out var error))
        {
            throw new LasFormatException(SceneGenerationErrorCodes.ModelAssetInvalid, error);
        }

        return info;
    }

    public static bool TryReadCopcInfo(
        ReadOnlySpan<byte> source,
        [NotNullWhen(true)] out CopcInfo? info,
        [NotNullWhen(false)] out string? error)
    {
        info = null;

        if (!TryValidateCopcPublicHeader(source, out var publicHeader, out error))
        {
            return false;
        }

        if (!TryReadCopcInfoVlr(source, publicHeader.HeaderSize, out var payload, out error))
        {
            return false;
        }

        return TryParseCopcPayload(source, publicHeader, payload, out info, out error);
    }

    private static bool TryValidateCopcPublicHeader(
        ReadOnlySpan<byte> source,
        out LasPublicHeader publicHeader,
        [NotNullWhen(false)] out string? error)
    {
        publicHeader = default;
        if (source.Length < Las14MinHeaderSize || !LasPublicHeader.TryRead(source, out publicHeader))
        {
            error = "Source is shorter than a LAS 1.4 public header or missing the 'LASF' signature.";
            return false;
        }

        if (publicHeader.VersionMajor != 1 || publicHeader.VersionMinor != 4)
        {
            error = "COPC requires LAS version 1.4.";
            return false;
        }

        if (!publicHeader.IsCompressed)
        {
            error = "COPC requires LAZ-compressed point data; raw uncompressed LAS is not a COPC file.";
            return false;
        }

        if (publicHeader.PointFormat is not (6 or 7 or 8))
        {
            error = "COPC requires LAS 1.4 point data record format 6, 7, or 8.";
            return false;
        }

        if (publicHeader.HeaderSize < Las14MinHeaderSize)
        {
            error = "LAS 1.4 header size is smaller than the minimum 375 bytes required for COPC.";
            return false;
        }

        var vlrCount = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(100, 4));
        if (vlrCount == 0)
        {
            error = "COPC file must contain a 'copc' info VLR immediately after the LAS 1.4 public header.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadCopcInfoVlr(
        ReadOnlySpan<byte> source,
        ushort headerSize,
        out ReadOnlySpan<byte> payload,
        [NotNullWhen(false)] out string? error)
    {
        payload = default;

        long vlrOffset = headerSize;
        if (vlrOffset + VlrHeaderLength > source.Length)
        {
            error = "COPC source is truncated before the first VLR header.";
            return false;
        }

        var vlrHeader = source.Slice((int)vlrOffset, VlrHeaderLength);
        var userId = vlrHeader.Slice(2, 16);
        var recordId = BinaryPrimitives.ReadUInt16LittleEndian(vlrHeader.Slice(18, 2));
        var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(vlrHeader.Slice(20, 2));

        if (!IsCopcUserId(userId) || recordId != CopcInfoRecordId)
        {
            error = "First VLR is not the COPC info VLR (user_id='copc', record_id=1).";
            return false;
        }

        if (recordLength < CopcInfoMinPayloadLength)
        {
            error = "COPC info VLR payload is shorter than the required 160 bytes.";
            return false;
        }

        var payloadOffset = vlrOffset + VlrHeaderLength;
        if (payloadOffset + recordLength > source.Length)
        {
            error = "COPC source is truncated inside the copc_info VLR payload.";
            return false;
        }

        payload = source.Slice((int)payloadOffset, recordLength);
        error = null;
        return true;
    }

    private static bool TryParseCopcPayload(
        ReadOnlySpan<byte> source,
        LasPublicHeader publicHeader,
        ReadOnlySpan<byte> payload,
        [NotNullWhen(true)] out CopcInfo? info,
        [NotNullWhen(false)] out string? error)
    {
        info = null;

        var centerX = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(0, 8));
        var centerY = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(8, 8));
        var centerZ = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(16, 8));
        var halfSize = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(24, 8));
        var spacing = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(32, 8));
        var rootHierOffset = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(40, 8));
        var rootHierSize = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(48, 8));
        var gpsTimeMin = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(56, 8));
        var gpsTimeMax = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(64, 8));

        if (!double.IsFinite(centerX) || !double.IsFinite(centerY) || !double.IsFinite(centerZ)
            || !double.IsFinite(halfSize) || halfSize <= 0
            || !double.IsFinite(spacing) || spacing <= 0
            || rootHierSize == 0)
        {
            error = "COPC info VLR contains invalid spatial octree bounds or root hierarchy metadata.";
            return false;
        }

        var offsetToPointData = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(96, 4));
        ulong pointCount = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(107, 4));
        var extendedPointCount = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(247, 8));
        if (extendedPointCount != 0)
        {
            pointCount = extendedPointCount;
        }

        info = new CopcInfo
        {
            VersionMajor = publicHeader.VersionMajor,
            VersionMinor = publicHeader.VersionMinor,
            PointDataRecordFormat = publicHeader.PointFormat,
            PointCount = pointCount,
            HeaderSize = publicHeader.HeaderSize,
            OffsetToPointData = offsetToPointData,
            MaxX = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(179, 8)),
            MinX = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(187, 8)),
            MaxY = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(195, 8)),
            MinY = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(203, 8)),
            MaxZ = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(211, 8)),
            MinZ = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(219, 8)),
            CenterX = centerX,
            CenterY = centerY,
            CenterZ = centerZ,
            HalfSize = halfSize,
            Spacing = spacing,
            RootHierarchyOffset = rootHierOffset,
            RootHierarchySize = rootHierSize,
            GpsTimeMinimum = gpsTimeMin,
            GpsTimeMaximum = gpsTimeMax
        };
        error = null;
        return true;
    }

    private static bool IsCopcUserId(ReadOnlySpan<byte> userId)
    {
        if (userId.Length < CopcUserId.Length || !userId[..CopcUserId.Length].SequenceEqual(CopcUserId))
        {
            return false;
        }

        for (var i = CopcUserId.Length; i < userId.Length; i++)
        {
            if (userId[i] != 0)
            {
                return false;
            }
        }

        return true;
    }
}

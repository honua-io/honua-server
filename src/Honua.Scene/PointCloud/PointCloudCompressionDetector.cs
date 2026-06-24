// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.PointCloud;

/// <summary>
/// Non-throwing companion to <see cref="LasPointCloudReader"/> that classifies an
/// uploaded point-cloud buffer without parsing it (#1854). The ingest dispatch
/// path uses it to decide, before any tiling, whether a buffer must first be
/// routed through the out-of-tree <c>pcloud.translate</c> worker (LAZ/COPC
/// decompression) rather than being rejected outright.
/// </summary>
/// <remarks>
/// The pure-managed <see cref="LasPointCloudReader"/> can only parse uncompressed
/// LAS; it throws <see cref="LasFormatException"/> for a compressed buffer. The
/// detector reads the single point-data-record-format byte and reports the
/// compression flag so the caller can pre-emptively dispatch decompression
/// instead of catching the reader's exception, keeping the rejected-vs-dispatch
/// decision explicit at the ingest boundary.
/// </remarks>
public static class PointCloudCompressionDetector
{
    // The LAS public header carries the point-data-record-format byte at offset
    // 104. laszip sets bit 7 (0x80) to flag LAZ arithmetic compression; bit 6
    // (0x40) is the COPC indicator. The base format lives in the low 6 bits.
    private const int PointFormatByteOffset = 104;
    private const byte LazCompressionFlag = 0x80;
    private const byte CopcIndicatorFlag = 0x40;
    private const int LasfSignatureLength = 4;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="source"/> is a LASF
    /// buffer whose point-data-record-format byte carries the LAZ or COPC
    /// compression flag. Returns <see langword="false"/> for an uncompressed LAS,
    /// a non-LASF buffer, or a buffer too short to carry the format byte; those
    /// are left for <see cref="LasPointCloudReader"/> to accept or reject.
    /// </summary>
    public static bool IsCompressed(ReadOnlySpan<byte> source)
    {
        if (source.Length <= PointFormatByteOffset)
        {
            return false;
        }

        if (!HasLasfSignature(source))
        {
            return false;
        }

        var rawPointFormat = source[PointFormatByteOffset];
        return (rawPointFormat & LazCompressionFlag) != 0 || (rawPointFormat & CopcIndicatorFlag) != 0;
    }

    /// <summary>
    /// Returns whether <paramref name="source"/> begins with the ASPRS
    /// <c>LASF</c> public-header signature. A buffer lacking it is neither LAS nor
    /// LAZ/COPC and is left for the reader to reject with its stable error.
    /// </summary>
    public static bool HasLasfSignature(ReadOnlySpan<byte> source)
        => source.Length >= LasfSignatureLength
            && source[0] == (byte)'L'
            && source[1] == (byte)'A'
            && source[2] == (byte)'S'
            && source[3] == (byte)'F';
}

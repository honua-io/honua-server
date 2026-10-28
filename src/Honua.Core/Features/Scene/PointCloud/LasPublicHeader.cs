// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Core.Features.Scene.PointCloud;

/// <summary>Bounded LAS public-header facts shared by dispatch and native conversion.</summary>
/// <param name="VersionMajor">Declared LAS major version.</param>
/// <param name="VersionMinor">Declared LAS minor version.</param>
/// <param name="HeaderSize">Declared public-header byte length.</param>
/// <param name="RawPointFormat">Point format including the LAZ compression bits.</param>
public readonly record struct LasPublicHeader(byte VersionMajor, byte VersionMinor, ushort HeaderSize, byte RawPointFormat)
{
    /// <summary>Gets the point record format without LAZ compression bits.</summary>
    public byte PointFormat => (byte)(RawPointFormat & 0x3F);

    /// <summary>Gets whether either LAZ compression bit is set; this does not identify COPC.</summary>
    public bool IsCompressed => (RawPointFormat & 0xC0) != 0;

    /// <summary>Tests the LASF signature without requiring a complete header.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> source) => source.StartsWith("LASF"u8);

    /// <summary>
    /// Inspects fixed header fields through byte 104 without allocating or decoding point records.
    /// This is not full LAS validation; callers validate supported versions and declared lengths.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out LasPublicHeader header)
    {
        header = default;
        if (source.Length < 105 || !HasSignature(source))
        {
            return false;
        }

        header = new LasPublicHeader(source[24], source[25],
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(94, 2)), source[104]);
        return true;
    }
}

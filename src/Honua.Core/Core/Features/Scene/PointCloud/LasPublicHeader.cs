// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Core.Features.Scene.PointCloud;

public readonly record struct LasPublicHeader(byte VersionMajor, byte VersionMinor, ushort HeaderSize, byte RawPointFormat)
{
    public byte PointFormat => (byte)(RawPointFormat & 0x3F);

    public bool IsCompressed => (RawPointFormat & 0xC0) != 0;

    public static bool HasSignature(ReadOnlySpan<byte> source) => source.StartsWith("LASF"u8);

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

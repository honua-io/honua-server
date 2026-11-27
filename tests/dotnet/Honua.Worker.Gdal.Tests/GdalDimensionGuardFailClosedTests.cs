// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

public sealed class GdalDimensionGuardFailClosedTests
{
    private const int TagImageWidth = 256;
    private const int TagImageLength = 257;
    private const int TypeByte = 1;
    private const int TypeShort = 3;
    private const int TypeLong = 4;
    private const int TypeSByte = 6;
    private const int TypeSShort = 8;
    private const int TypeSLong = 9;
    private const int TypeLong8 = 16;
    private const int TypeSLong8 = 17;

    private readonly record struct ClassicTag(int Tag, int Type, uint Value);

    private readonly record struct BigTag(int Tag, int Type, ulong Value);

    private static GdalWorkerOptions Options() => new();

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(new byte[] { 0x49, 0x49, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x49, 0x49, 0x29, 0x00, 0x08, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x4D, 0x4D, 0x00, 0x29, 0x00, 0x00, 0x00, 0x08 })]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x01, 0x08, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x4D, 0x4D, 0x01, 0x2A, 0x00, 0x00, 0x00, 0x08 })]
    [InlineData(new byte[] { (byte)'I', (byte)'I', (byte)'t', (byte)'e', (byte)'x', (byte)'t' })]
    [InlineData(new byte[] { (byte)'M', (byte)'M', (byte)'d', (byte)'a', (byte)'t', (byte)'a' })]
    public void ClassifyAndTryAdmit_BogusIiOrMmMagic_IsClassifiedUnknownAndRejected(byte[] payload)
    {
        GdalRasterDimensionGuard.ClassifyContainer(payload)
            .Should().Be(GdalRasterDimensionGuard.RasterContainerFormat.Unknown);
        GdalRasterDimensionGuard.TryAdmit(payload, Options(), out var error).Should().BeFalse();
        error.Should().Contain("could not be identified");
    }

    [UnitTest]
    public void TryAdmit_TruncatedClassicTiffHeaders_RejectFailClosed()
    {
        byte[] headerOnly = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00];
        byte[] truncatedIfd = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x01];

        GdalRasterDimensionGuard.TryAdmit(headerOnly, Options(), out var headerError).Should().BeFalse();
        headerError.Should().Contain("malformed");

        GdalRasterDimensionGuard.TryAdmit(truncatedIfd, Options(), out var ifdError).Should().BeFalse();
        ifdError.Should().Contain("malformed");
    }

    [UnitTest]
    public void TryAdmit_TruncatedBigTiffPngAndJpegHeaders_RejectFailClosed()
    {
        byte[] truncatedBigTiff = [0x49, 0x49, 0x2B, 0x00, 0x08, 0x00, 0x00, 0x00, 0x10, 0x00];
        byte[] truncatedPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
        byte[] jpegWithoutSof = [0xFF, 0xD8, 0xFF, 0xD9];

        GdalRasterDimensionGuard.TryAdmit(truncatedBigTiff, Options(), out _).Should().BeFalse();
        GdalRasterDimensionGuard.TryAdmit(truncatedPng, Options(), out _).Should().BeFalse();
        GdalRasterDimensionGuard.TryAdmit(jpegWithoutSof, Options(), out _).Should().BeFalse();
    }

    [UnitTest]
    public void TryAdmit_DuplicateImageWidthLargeThenSmall_TracksMaximumAndRejects()
    {
        var tiff = BuildClassicTiff(
            new ClassicTag(TagImageWidth, TypeLong, 1_000_000),
            new ClassicTag(TagImageWidth, TypeShort, 16),
            new ClassicTag(TagImageLength, TypeLong, 16));

        GdalRasterDimensionGuard.TryReadGeoTiffDimensions(tiff, out var dims).Should().BeTrue();
        dims.Width.Should().Be(1_000_000);
        GdalRasterDimensionGuard.TryAdmit(tiff, Options(), out var error).Should().BeFalse();
        error.Should().Contain("MaxRasterWidth");
    }

    [UnitTest]
    public void TryAdmit_DuplicateImageLengthLargeThenSmall_TracksMaximumAndRejects()
    {
        var tiff = BuildClassicTiff(
            new ClassicTag(TagImageWidth, TypeLong, 16),
            new ClassicTag(TagImageLength, TypeLong, 500_000),
            new ClassicTag(TagImageLength, TypeByte, 8));

        GdalRasterDimensionGuard.TryReadGeoTiffDimensions(tiff, out var dims).Should().BeTrue();
        dims.Height.Should().Be(500_000);
        GdalRasterDimensionGuard.TryAdmit(tiff, Options(), out var error).Should().BeFalse();
        error.Should().Contain("MaxRasterHeight");
    }

    [UnitTest]
    public void TryAdmit_OversizedBigTiffIfdEntryCount_RejectsFailClosed()
    {
        var entryCount = GdalTiffDimensionReader.MaxIfdEntries + 1;
        var bytes = new byte[16 + 8 + (entryCount * 20)];
        bytes[0] = 0x49;
        bytes[1] = 0x49;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 43);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8, 8), 16);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16, 8), (ulong)entryCount);

        GdalRasterDimensionGuard.TryReadGeoTiffDimensions(bytes, out _).Should().BeFalse();
        GdalRasterDimensionGuard.TryAdmit(bytes, Options(), out var error).Should().BeFalse();
        error.Should().Contain("malformed");
    }

    [UnitTest]
    public void TryAdmit_OversizedClassicTiffIfdEntryCount_RejectsFailClosed()
    {
        var entryCount = GdalTiffDimensionReader.MaxIfdEntries + 1;
        var bytes = new byte[8 + 2 + (entryCount * 12)];
        bytes[0] = 0x49;
        bytes[1] = 0x49;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), (ushort)entryCount);

        GdalRasterDimensionGuard.TryReadGeoTiffDimensions(bytes, out _).Should().BeFalse();
        GdalRasterDimensionGuard.TryAdmit(bytes, Options(), out var error).Should().BeFalse();
        error.Should().Contain("malformed");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(TypeByte, 64u, 32u)]
    [InlineData(TypeShort, 640u, 480u)]
    [InlineData(TypeLong, 2048u, 1024u)]
    [InlineData(TypeSByte, 100u, 50u)]
    [InlineData(TypeSShort, 1200u, 900u)]
    [InlineData(TypeSLong, 4096u, 2048u)]
    public void TryAdmit_ClassicTiffIntegerTagTypes_AreParsedAndAdmitted(int tagType, uint width, uint height)
    {
        var tiff = BuildClassicTiff(
            new ClassicTag(TagImageWidth, tagType, width),
            new ClassicTag(TagImageLength, tagType, height));

        GdalRasterDimensionGuard.TryReadGeoTiffDimensions(tiff, out var dims).Should().BeTrue();
        dims.Width.Should().Be(width);
        dims.Height.Should().Be(height);
        GdalRasterDimensionGuard.TryAdmit(tiff, Options(), out var error).Should().BeTrue(error);
    }

    [UnitTest]
    public void TryAdmit_ClassicTiffOverCapSLongDimension_IsRejected()
    {
        var tiff = BuildClassicTiff(
            new ClassicTag(TagImageWidth, TypeSLong, 250_000),
            new ClassicTag(TagImageLength, TypeSLong, 16));

        GdalRasterDimensionGuard.TryAdmit(tiff, Options(), out var error).Should().BeFalse();
        error.Should().Contain("MaxRasterWidth");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(TypeLong8, 1024UL, 512UL)]
    [InlineData(TypeSLong8, 2048UL, 768UL)]
    public void TryAdmit_BigTiff64BitIntegerTagTypes_AreParsedAndAdmitted(int tagType, ulong width, ulong height)
    {
        var tiff = BuildBigTiff(
            new BigTag(TagImageWidth, tagType, width),
            new BigTag(TagImageLength, tagType, height));

        GdalRasterDimensionGuard.TryReadGeoTiffDimensions(tiff, out var dims).Should().BeTrue();
        dims.Width.Should().Be((long)width);
        dims.Height.Should().Be((long)height);
        GdalRasterDimensionGuard.TryAdmit(tiff, Options(), out var error).Should().BeTrue(error);
    }

    [UnitTest]
    public void TryAdmit_NegativeSignedIntegerDimensionTags_RejectFailClosed()
    {
        var negSByte = BuildClassicTiff(
            new ClassicTag(TagImageWidth, TypeSByte, unchecked((byte)(sbyte)-10)),
            new ClassicTag(TagImageLength, TypeShort, 16));
        var negSShort = BuildClassicTiff(
            new ClassicTag(TagImageWidth, TypeSShort, unchecked((ushort)(short)-100)),
            new ClassicTag(TagImageLength, TypeShort, 16));
        var negSLong = BuildClassicTiff(
            new ClassicTag(TagImageWidth, TypeSLong, unchecked((uint)-500)),
            new ClassicTag(TagImageLength, TypeShort, 16));
        var negSLong8 = BuildBigTiff(
            new BigTag(TagImageWidth, TypeSLong8, unchecked((ulong)-1000L)),
            new BigTag(TagImageLength, TypeLong8, 16));

        GdalRasterDimensionGuard.TryAdmit(negSByte, Options(), out _).Should().BeFalse();
        GdalRasterDimensionGuard.TryAdmit(negSShort, Options(), out _).Should().BeFalse();
        GdalRasterDimensionGuard.TryAdmit(negSLong, Options(), out _).Should().BeFalse();
        GdalRasterDimensionGuard.TryAdmit(negSLong8, Options(), out _).Should().BeFalse();
    }

    private static byte[] BuildClassicTiff(params ClassicTag[] tags)
    {
        var bytes = new byte[8 + 2 + (tags.Length * 12) + 4];
        bytes[0] = 0x49;
        bytes[1] = 0x49;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), (ushort)tags.Length);

        for (var i = 0; i < tags.Length; i++)
        {
            var offset = 10 + (i * 12);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), (ushort)tags[i].Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2, 2), (ushort)tags[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4, 4), 1);
            WriteClassicValue(bytes.AsSpan(offset + 8, 4), tags[i].Type, tags[i].Value);
        }

        return bytes;
    }

    private static void WriteClassicValue(Span<byte> target, int type, uint value)
    {
        switch (type)
        {
            case TypeByte:
            case TypeSByte:
                target[0] = (byte)value;
                break;
            case TypeShort:
            case TypeSShort:
                BinaryPrimitives.WriteUInt16LittleEndian(target[..2], (ushort)value);
                break;
            default:
                BinaryPrimitives.WriteUInt32LittleEndian(target, value);
                break;
        }
    }

    private static byte[] BuildBigTiff(params BigTag[] tags)
    {
        var bytes = new byte[16 + 8 + (tags.Length * 20) + 8];
        bytes[0] = 0x49;
        bytes[1] = 0x49;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 43);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8, 8), 16);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16, 8), (ulong)tags.Length);

        for (var i = 0; i < tags.Length; i++)
        {
            var offset = 24 + (i * 20);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), (ushort)tags[i].Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2, 2), (ushort)tags[i].Type);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 4, 8), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 12, 8), tags[i].Value);
        }

        return bytes;
    }
}

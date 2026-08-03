// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Geoprocessing.Inference;

namespace Honua.Server.Tests.Features.Geoprocessing.Inference;

public enum TiffContainer
{
    Classic,
    BigTiff
}

public enum TiffByteOrder
{
    LittleEndian,
    BigEndian
}

public enum StructuredTiffMutation
{
    None,
    ModelTagWrongType,
    ModelScaleCountTooSmall,
    ModelTagCountExceedsPayload,
    ModelTagOffsetOutsidePayload,
    IfdEncodedWithOppositeByteOrder,
    UnsupportedHeaderVersion,
    MixedEndianHeader,
    GeoKeyCountMismatch,
    GeoKeysOutOfOrderWithDuplicate,
    TiepointCountNotMultipleOfSix,
    StorageOffsetOutsidePayload,
    SelfReferentialIfd,
    NestedIfd,
    DuplicateConflictingTag,
    NegativeZeroScale,
    SubnormalScale
}

public readonly record struct ValidGeoTiffInput(
    TiffContainer Container,
    TiffByteOrder ByteOrder,
    int Width,
    int Height,
    int OriginX,
    int OriginY,
    int PixelSizeX,
    int PixelSizeY,
    ushort Epsg,
    bool PixelIsPoint);

public readonly record struct StructuredTiffInput(
    TiffContainer Container,
    TiffByteOrder ByteOrder,
    StructuredTiffMutation Mutation,
    int Seed);

internal readonly record struct GeoTiffFixture(
    byte[] Bytes,
    GeoTiffGeoreferencing Expected);

internal readonly record struct GeoTiffRegressionCase(
    string Name,
    byte[] Bytes,
    bool ExpectedToParse,
    bool ExpectedToBeGeoreferenced);

/// <summary>
/// Builds small, deterministic TIFF structures without a decoder dependency.
/// The builders deliberately describe only metadata and a bounded 64-byte
/// storage region: pixel decoding is outside the georeferencing reader's scope.
/// </summary>
internal static class GeoTiffFuzzCorpus
{
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagStripOffsets = 273;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagTileOffsets = 324;
    private const ushort TagTileByteCounts = 325;
    private const ushort TagModelPixelScale = 33550;
    private const ushort TagModelTiepoint = 33922;
    private const ushort TagModelTransformation = 34264;
    private const ushort TagGeoKeyDirectory = 34735;

    private const ushort TiffTypeShort = 3;
    private const ushort TiffTypeLong = 4;
    private const ushort TiffTypeDouble = 12;

    private const int RasterStorageBytes = 64;
    private const int GeoKeyCapacityBytes = 32;

    public static GeoTiffFixture CreateValid(ValidGeoTiffInput input)
    {
        var layout = BuildScaleTiff(
            input.Container,
            input.ByteOrder,
            input.Width,
            input.Height,
            input.OriginX,
            input.OriginY,
            input.PixelSizeX,
            input.PixelSizeY,
            input.Epsg,
            input.PixelIsPoint ? (ushort)2 : (ushort)1,
            StorageShape.SingleStrip);

        var normalizedOriginX = input.PixelIsPoint
            ? input.OriginX - (input.PixelSizeX / 2d)
            : input.OriginX;
        var normalizedOriginY = input.PixelIsPoint
            ? input.OriginY + (input.PixelSizeY / 2d)
            : input.OriginY;

        return new GeoTiffFixture(
            layout.Bytes,
            new GeoTiffGeoreferencing
            {
                Width = input.Width,
                Height = input.Height,
                OriginX = normalizedOriginX,
                OriginY = normalizedOriginY,
                PixelSizeX = input.PixelSizeX,
                PixelSizeY = input.PixelSizeY,
                CrsCode = input.Epsg,
                HasRasterData = true
            });
    }

    public static byte[] CreateMutated(StructuredTiffInput input)
    {
        var magnitude = Math.Abs((long)input.Seed);
        var width = 8 + (int)(magnitude % 121);
        var height = 8 + (int)((magnitude / 7) % 121);
        var pixelSize = 1 + (int)((magnitude / 13) % 30);
        var layout = BuildScaleTiff(
            input.Container,
            input.ByteOrder,
            width,
            height,
            originX: -1_000_000 + (magnitude % 2_000_000),
            originY: -1_000_000 + ((magnitude / 17) % 2_000_000),
            pixelSizeX: pixelSize,
            pixelSizeY: pixelSize,
            epsg: (ushort)(32601 + (magnitude % 60)),
            rasterType: 1,
            storageShape: StorageShape.SingleStrip);

        ApplyMutation(layout, input.Mutation);
        return layout.Bytes;
    }

    public static IEnumerable<GeoTiffRegressionCase> RegressionCases()
    {
        yield return Case(
            "classic-header-only-no-storage",
            BuildScaleTiff(
                TiffContainer.Classic,
                TiffByteOrder.LittleEndian,
                32,
                32,
                500_000,
                4_600_000,
                10,
                10,
                32610,
                1,
                StorageShape.None).Bytes,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return MutatedCase(
            "strip-offset-outside-payload",
            TiffContainer.Classic,
            StructuredTiffMutation.StorageOffsetOutsidePayload,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return Case(
            "multi-strip-second-segment-truncated",
            BuildScaleTiff(
                TiffContainer.Classic,
                TiffByteOrder.LittleEndian,
                32,
                32,
                500_000,
                4_600_000,
                10,
                10,
                32610,
                1,
                StorageShape.MultiStripTruncated).Bytes,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return Case(
            "tiled-second-segment-truncated",
            BuildScaleTiff(
                TiffContainer.Classic,
                TiffByteOrder.LittleEndian,
                32,
                32,
                500_000,
                4_600_000,
                10,
                10,
                32610,
                1,
                StorageShape.TiledTruncated).Bytes,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return Case(
            "bigtiff-ifd-offset-near-ulong-max",
            BigTiffWithOverflowingIfdOffset(),
            expectedToParse: false,
            expectedToBeGeoreferenced: false);

        yield return MutatedCase(
            "declared-model-element-count-exceeds-payload",
            TiffContainer.BigTiff,
            StructuredTiffMutation.ModelTagCountExceedsPayload,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return MutatedCase(
            "model-tag-wrong-field-type",
            TiffContainer.Classic,
            StructuredTiffMutation.ModelTagWrongType,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return MutatedCase(
            "model-pixel-scale-count-two",
            TiffContainer.Classic,
            StructuredTiffMutation.ModelScaleCountTooSmall,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return Case(
            "non-finite-model-scale",
            WithScale(double.PositiveInfinity, 10),
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return Case(
            "non-finite-model-tiepoint",
            WithTiepoint(double.NaN, 4_600_000),
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return Case(
            "rotated-model-transformation",
            BuildMatrixTiff(scaleX: 10, scaleY: -10, shearX: 0.25, shearY: 0).Bytes,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return Case(
            "sheared-model-transformation",
            BuildMatrixTiff(scaleX: 10, scaleY: -10, shearX: 0, shearY: 0.25).Bytes,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return Case(
            "x-axis-flipped-model-transformation",
            BuildMatrixTiff(scaleX: -10, scaleY: -10, shearX: 0, shearY: 0).Bytes,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return Case(
            "y-axis-flipped-model-transformation",
            BuildMatrixTiff(scaleX: 10, scaleY: 10, shearX: 0, shearY: 0).Bytes,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return Case(
            "model-transformation-count-seventeen",
            WithMatrixCount(17),
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return Case(
            "pixel-is-area",
            BuildScaleTiff(
                TiffContainer.Classic,
                TiffByteOrder.LittleEndian,
                32,
                32,
                500_000,
                4_600_000,
                10,
                10,
                32610,
                1,
                StorageShape.SingleStrip).Bytes,
            expectedToParse: true,
            expectedToBeGeoreferenced: true);

        yield return Case(
            "pixel-is-point",
            BuildScaleTiff(
                TiffContainer.Classic,
                TiffByteOrder.LittleEndian,
                32,
                32,
                500_000,
                4_600_000,
                10,
                10,
                32610,
                2,
                StorageShape.SingleStrip).Bytes,
            expectedToParse: true,
            expectedToBeGeoreferenced: true);

        yield return Case(
            "user-defined-crs-32767",
            BuildScaleTiff(
                TiffContainer.Classic,
                TiffByteOrder.LittleEndian,
                32,
                32,
                500_000,
                4_600_000,
                10,
                10,
                32767,
                1,
                StorageShape.SingleStrip).Bytes,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return MutatedCase(
            "self-referential-next-ifd",
            TiffContainer.Classic,
            StructuredTiffMutation.SelfReferentialIfd,
            expectedToParse: true,
            expectedToBeGeoreferenced: true);

        yield return MutatedCase(
            "nested-next-ifd",
            TiffContainer.BigTiff,
            StructuredTiffMutation.NestedIfd,
            expectedToParse: true,
            expectedToBeGeoreferenced: true);

        yield return MutatedCase(
            "duplicate-conflicting-image-width-tag",
            TiffContainer.Classic,
            StructuredTiffMutation.DuplicateConflictingTag,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return MutatedCase(
            "mixed-endian-header",
            TiffContainer.BigTiff,
            StructuredTiffMutation.MixedEndianHeader,
            expectedToParse: false,
            expectedToBeGeoreferenced: false);

        yield return MutatedCase(
            "subnormal-model-scale",
            TiffContainer.Classic,
            StructuredTiffMutation.SubnormalScale,
            expectedToParse: true,
            expectedToBeGeoreferenced: true);

        yield return MutatedCase(
            "negative-zero-model-scale",
            TiffContainer.BigTiff,
            StructuredTiffMutation.NegativeZeroScale,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return MutatedCase(
            "tiepoint-count-not-multiple-of-six",
            TiffContainer.Classic,
            StructuredTiffMutation.TiepointCountNotMultipleOfSix,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return MutatedCase(
            "geokey-header-key-count-mismatch",
            TiffContainer.Classic,
            StructuredTiffMutation.GeoKeyCountMismatch,
            expectedToParse: true,
            expectedToBeGeoreferenced: false);

        yield return MutatedCase(
            "out-of-order-duplicate-geokeys",
            TiffContainer.BigTiff,
            StructuredTiffMutation.GeoKeysOutOfOrderWithDuplicate,
            expectedToParse: true,
            expectedToBeGeoreferenced: true);
    }

    private static GeoTiffRegressionCase MutatedCase(
        string name,
        TiffContainer container,
        StructuredTiffMutation mutation,
        bool expectedToParse,
        bool expectedToBeGeoreferenced)
        => Case(
            name,
            CreateMutated(new StructuredTiffInput(container, TiffByteOrder.LittleEndian, mutation, 3049)),
            expectedToParse,
            expectedToBeGeoreferenced);

    private static GeoTiffRegressionCase Case(
        string name,
        byte[] bytes,
        bool expectedToParse,
        bool expectedToBeGeoreferenced)
        => new(name, bytes, expectedToParse, expectedToBeGeoreferenced);

    private static byte[] WithScale(double scaleX, double scaleY)
    {
        var layout = BuildScaleTiff(
            TiffContainer.Classic,
            TiffByteOrder.LittleEndian,
            32,
            32,
            500_000,
            4_600_000,
            10,
            10,
            32610,
            1,
            StorageShape.SingleStrip);
        WriteDouble(layout.Bytes, layout.ScaleOffset, scaleX, layout.ByteOrder);
        WriteDouble(layout.Bytes, layout.ScaleOffset + 8, scaleY, layout.ByteOrder);
        return layout.Bytes;
    }

    private static byte[] WithTiepoint(double originX, double originY)
    {
        var layout = BuildScaleTiff(
            TiffContainer.Classic,
            TiffByteOrder.LittleEndian,
            32,
            32,
            500_000,
            4_600_000,
            10,
            10,
            32610,
            1,
            StorageShape.SingleStrip);
        WriteDouble(layout.Bytes, layout.TiepointOffset + 24, originX, layout.ByteOrder);
        WriteDouble(layout.Bytes, layout.TiepointOffset + 32, originY, layout.ByteOrder);
        return layout.Bytes;
    }

    private static byte[] WithMatrixCount(ulong count)
    {
        var layout = BuildMatrixTiff(scaleX: 10, scaleY: -10, shearX: 0, shearY: 0);
        WriteCount(
            layout.Bytes,
            layout.EntryOffsets[TagModelTransformation] + 4,
            count,
            layout.Container,
            layout.ByteOrder);
        return layout.Bytes;
    }

    private static void ApplyMutation(TiffLayout layout, StructuredTiffMutation mutation)
    {
        switch (mutation)
        {
            case StructuredTiffMutation.None:
                return;
            case StructuredTiffMutation.ModelTagWrongType:
                WriteUInt16(
                    layout.Bytes,
                    layout.EntryOffsets[TagModelPixelScale] + 2,
                    TiffTypeShort,
                    layout.ByteOrder);
                return;
            case StructuredTiffMutation.ModelScaleCountTooSmall:
                WriteCount(
                    layout.Bytes,
                    layout.EntryOffsets[TagModelPixelScale] + 4,
                    2,
                    layout.Container,
                    layout.ByteOrder);
                return;
            case StructuredTiffMutation.ModelTagCountExceedsPayload:
                WriteCount(
                    layout.Bytes,
                    layout.EntryOffsets[TagModelPixelScale] + 4,
                    layout.Container == TiffContainer.Classic ? uint.MaxValue : ulong.MaxValue,
                    layout.Container,
                    layout.ByteOrder);
                return;
            case StructuredTiffMutation.ModelTagOffsetOutsidePayload:
                WritePointer(
                    layout.Bytes,
                    ValueFieldOffset(layout, TagModelPixelScale),
                    ulong.MaxValue,
                    layout.Container,
                    layout.ByteOrder);
                return;
            case StructuredTiffMutation.IfdEncodedWithOppositeByteOrder:
                Array.Reverse(layout.Bytes, layout.IfdOffset, layout.Container == TiffContainer.Classic ? 2 : 8);
                return;
            case StructuredTiffMutation.UnsupportedHeaderVersion:
                WriteUInt16(layout.Bytes, 2, 44, layout.ByteOrder);
                return;
            case StructuredTiffMutation.MixedEndianHeader:
                layout.Bytes[0] = layout.ByteOrder == TiffByteOrder.LittleEndian ? (byte)0x4d : (byte)0x49;
                layout.Bytes[1] = layout.Bytes[0];
                return;
            case StructuredTiffMutation.GeoKeyCountMismatch:
                WriteUInt16(layout.Bytes, layout.GeoKeyOffset + 6, 1, layout.ByteOrder);
                return;
            case StructuredTiffMutation.GeoKeysOutOfOrderWithDuplicate:
                WriteCount(
                    layout.Bytes,
                    layout.EntryOffsets[TagGeoKeyDirectory] + 4,
                    16,
                    layout.Container,
                    layout.ByteOrder);
                WriteUInt16(layout.Bytes, layout.GeoKeyOffset + 6, 3, layout.ByteOrder);
                WriteGeoKey(layout.Bytes, layout.GeoKeyOffset + 8, 3072, 32610, layout.ByteOrder);
                WriteGeoKey(layout.Bytes, layout.GeoKeyOffset + 16, 1025, 1, layout.ByteOrder);
                WriteGeoKey(layout.Bytes, layout.GeoKeyOffset + 24, 3072, 32611, layout.ByteOrder);
                return;
            case StructuredTiffMutation.TiepointCountNotMultipleOfSix:
                WriteCount(
                    layout.Bytes,
                    layout.EntryOffsets[TagModelTiepoint] + 4,
                    7,
                    layout.Container,
                    layout.ByteOrder);
                return;
            case StructuredTiffMutation.StorageOffsetOutsidePayload:
                WriteInlineUnsigned(
                    layout.Bytes,
                    ValueFieldOffset(layout, TagStripOffsets),
                    (ulong)layout.Bytes.Length + 1,
                    TiffTypeLong,
                    layout.ByteOrder);
                return;
            case StructuredTiffMutation.SelfReferentialIfd:
                WritePointer(
                    layout.Bytes,
                    layout.NextIfdOffset,
                    (ulong)layout.IfdOffset,
                    layout.Container,
                    layout.ByteOrder);
                return;
            case StructuredTiffMutation.NestedIfd:
                WritePointer(
                    layout.Bytes,
                    layout.NextIfdOffset,
                    (ulong)layout.PixelDataOffset,
                    layout.Container,
                    layout.ByteOrder);
                if (layout.Container == TiffContainer.Classic)
                {
                    WriteUInt16(layout.Bytes, layout.PixelDataOffset, 0, layout.ByteOrder);
                    WriteUInt32(layout.Bytes, layout.PixelDataOffset + 2, 0, layout.ByteOrder);
                }
                else
                {
                    WriteUInt64(layout.Bytes, layout.PixelDataOffset, 0, layout.ByteOrder);
                    WriteUInt64(layout.Bytes, layout.PixelDataOffset + 8, 0, layout.ByteOrder);
                }

                return;
            case StructuredTiffMutation.DuplicateConflictingTag:
                {
                    var entryOffset = layout.EntryOffsets[TagGeoKeyDirectory];
                    WriteUInt16(layout.Bytes, entryOffset, TagImageWidth, layout.ByteOrder);
                    WriteUInt16(layout.Bytes, entryOffset + 2, TiffTypeLong, layout.ByteOrder);
                    WriteCount(layout.Bytes, entryOffset + 4, 1, layout.Container, layout.ByteOrder);
                    WriteInlineUnsigned(
                        layout.Bytes,
                        ValueFieldOffset(layout, TagGeoKeyDirectory),
                        4096,
                        TiffTypeLong,
                        layout.ByteOrder);
                    return;
                }
            case StructuredTiffMutation.NegativeZeroScale:
                WriteDouble(layout.Bytes, layout.ScaleOffset, -0d, layout.ByteOrder);
                return;
            case StructuredTiffMutation.SubnormalScale:
                WriteDouble(layout.Bytes, layout.ScaleOffset, double.Epsilon, layout.ByteOrder);
                WriteDouble(layout.Bytes, layout.ScaleOffset + 8, double.Epsilon, layout.ByteOrder);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    private static TiffLayout BuildScaleTiff(
        TiffContainer container,
        TiffByteOrder byteOrder,
        int width,
        int height,
        double originX,
        double originY,
        double pixelSizeX,
        double pixelSizeY,
        ushort epsg,
        ushort rasterType,
        StorageShape storageShape)
    {
        var includeStorage = storageShape != StorageShape.None;
        var segmentCount = storageShape is StorageShape.MultiStripTruncated or StorageShape.TiledTruncated ? 2 : 1;
        var entryCount = includeStorage ? 7 : 5;
        var headerBytes = container == TiffContainer.Classic ? 8 : 16;
        var countBytes = container == TiffContainer.Classic ? 2 : 8;
        var entryBytes = container == TiffContainer.Classic ? 12 : 20;
        var nextIfdBytes = container == TiffContainer.Classic ? 4 : 8;
        var ifdOffset = headerBytes;
        var dataStart = ifdOffset + countBytes + (entryCount * entryBytes) + nextIfdBytes;
        var segmentArraysBytes = includeStorage && segmentCount == 2 ? 16 : 0;
        var scaleOffset = dataStart + segmentArraysBytes;
        var tiepointOffset = scaleOffset + 24;
        var geoKeyOffset = tiepointOffset + 48;
        var pixelDataOffset = geoKeyOffset + GeoKeyCapacityBytes;
        var storedBytes = storageShape switch
        {
            StorageShape.None => 0,
            StorageShape.MultiStripTruncated or StorageShape.TiledTruncated => RasterStorageBytes / 2,
            _ => RasterStorageBytes
        };
        var totalLength = pixelDataOffset + storedBytes;
        var bytes = new byte[totalLength];

        WriteHeader(bytes, container, byteOrder, ifdOffset);
        WriteIfdEntryCount(bytes, ifdOffset, (ulong)entryCount, container, byteOrder);

        var entryOffsets = new Dictionary<ushort, int>();
        var entry = ifdOffset + countBytes;
        entry = WriteEntry(
            bytes,
            entryOffsets,
            entry,
            TagImageWidth,
            TiffTypeLong,
            1,
            (uint)width,
            inline: true,
            container,
            byteOrder);
        entry = WriteEntry(
            bytes,
            entryOffsets,
            entry,
            TagImageLength,
            TiffTypeLong,
            1,
            (uint)height,
            inline: true,
            container,
            byteOrder);

        if (includeStorage)
        {
            var offsetsTag = storageShape == StorageShape.TiledTruncated ? TagTileOffsets : TagStripOffsets;
            var byteCountsTag = storageShape == StorageShape.TiledTruncated ? TagTileByteCounts : TagStripByteCounts;
            var offsetsValue = segmentCount == 1 ? (ulong)pixelDataOffset : (ulong)dataStart;
            var countsValue = segmentCount == 1 ? RasterStorageBytes : dataStart + 8;
            entry = WriteEntry(
                bytes,
                entryOffsets,
                entry,
                offsetsTag,
                TiffTypeLong,
                (ulong)segmentCount,
                offsetsValue,
                inline: segmentCount == 1,
                container,
                byteOrder);
            entry = WriteEntry(
                bytes,
                entryOffsets,
                entry,
                byteCountsTag,
                TiffTypeLong,
                (ulong)segmentCount,
                (ulong)countsValue,
                inline: segmentCount == 1,
                container,
                byteOrder);

            if (segmentCount == 2)
            {
                WriteUInt32(bytes, dataStart, (uint)pixelDataOffset, byteOrder);
                WriteUInt32(bytes, dataStart + 4, (uint)(pixelDataOffset + (RasterStorageBytes / 2)), byteOrder);
                WriteUInt32(bytes, dataStart + 8, RasterStorageBytes / 2, byteOrder);
                WriteUInt32(bytes, dataStart + 12, RasterStorageBytes / 2, byteOrder);
            }
        }

        entry = WriteEntry(
            bytes,
            entryOffsets,
            entry,
            TagModelPixelScale,
            TiffTypeDouble,
            3,
            (ulong)scaleOffset,
            inline: false,
            container,
            byteOrder);
        entry = WriteEntry(
            bytes,
            entryOffsets,
            entry,
            TagModelTiepoint,
            TiffTypeDouble,
            6,
            (ulong)tiepointOffset,
            inline: false,
            container,
            byteOrder);
        entry = WriteEntry(
            bytes,
            entryOffsets,
            entry,
            TagGeoKeyDirectory,
            TiffTypeShort,
            12,
            (ulong)geoKeyOffset,
            inline: false,
            container,
            byteOrder);

        var nextIfdOffset = entry;
        WritePointer(bytes, nextIfdOffset, 0, container, byteOrder);

        WriteDouble(bytes, scaleOffset, pixelSizeX, byteOrder);
        WriteDouble(bytes, scaleOffset + 8, pixelSizeY, byteOrder);
        WriteDouble(bytes, scaleOffset + 16, 0, byteOrder);
        WriteDouble(bytes, tiepointOffset, 0, byteOrder);
        WriteDouble(bytes, tiepointOffset + 8, 0, byteOrder);
        WriteDouble(bytes, tiepointOffset + 16, 0, byteOrder);
        WriteDouble(bytes, tiepointOffset + 24, originX, byteOrder);
        WriteDouble(bytes, tiepointOffset + 32, originY, byteOrder);
        WriteDouble(bytes, tiepointOffset + 40, 0, byteOrder);
        WriteGeoKeyDirectory(bytes, geoKeyOffset, rasterType, epsg, byteOrder);

        for (var offset = pixelDataOffset; offset < bytes.Length; offset++)
        {
            bytes[offset] = (byte)((offset - pixelDataOffset) % 251);
        }

        return new TiffLayout(
            bytes,
            container,
            byteOrder,
            ifdOffset,
            nextIfdOffset,
            scaleOffset,
            tiepointOffset,
            geoKeyOffset,
            pixelDataOffset,
            entryOffsets);
    }

    private static TiffLayout BuildMatrixTiff(double scaleX, double scaleY, double shearX, double shearY)
    {
        const TiffContainer container = TiffContainer.Classic;
        const TiffByteOrder byteOrder = TiffByteOrder.LittleEndian;
        const int ifdOffset = 8;
        const int entryCount = 6;
        const int dataStart = ifdOffset + 2 + (entryCount * 12) + 4;
        const int matrixOffset = dataStart;
        const int geoKeyOffset = matrixOffset + 128;
        const int pixelDataOffset = geoKeyOffset + GeoKeyCapacityBytes;
        var bytes = new byte[pixelDataOffset + RasterStorageBytes];

        WriteHeader(bytes, container, byteOrder, ifdOffset);
        WriteIfdEntryCount(bytes, ifdOffset, entryCount, container, byteOrder);
        var entries = new Dictionary<ushort, int>();
        var entry = ifdOffset + 2;
        entry = WriteEntry(bytes, entries, entry, TagImageWidth, TiffTypeLong, 1, 32, true, container, byteOrder);
        entry = WriteEntry(bytes, entries, entry, TagImageLength, TiffTypeLong, 1, 32, true, container, byteOrder);
        entry = WriteEntry(
            bytes,
            entries,
            entry,
            TagStripOffsets,
            TiffTypeLong,
            1,
            pixelDataOffset,
            true,
            container,
            byteOrder);
        entry = WriteEntry(
            bytes,
            entries,
            entry,
            TagStripByteCounts,
            TiffTypeLong,
            1,
            RasterStorageBytes,
            true,
            container,
            byteOrder);
        entry = WriteEntry(
            bytes,
            entries,
            entry,
            TagModelTransformation,
            TiffTypeDouble,
            16,
            matrixOffset,
            false,
            container,
            byteOrder);
        entry = WriteEntry(
            bytes,
            entries,
            entry,
            TagGeoKeyDirectory,
            TiffTypeShort,
            12,
            geoKeyOffset,
            false,
            container,
            byteOrder);
        WritePointer(bytes, entry, 0, container, byteOrder);

        WriteDouble(bytes, matrixOffset, scaleX, byteOrder);
        WriteDouble(bytes, matrixOffset + 8, shearX, byteOrder);
        WriteDouble(bytes, matrixOffset + 24, 500_000, byteOrder);
        WriteDouble(bytes, matrixOffset + 32, shearY, byteOrder);
        WriteDouble(bytes, matrixOffset + 40, scaleY, byteOrder);
        WriteDouble(bytes, matrixOffset + 56, 4_600_000, byteOrder);
        WriteGeoKeyDirectory(bytes, geoKeyOffset, 1, 32610, byteOrder);

        return new TiffLayout(
            bytes,
            container,
            byteOrder,
            ifdOffset,
            entry,
            0,
            0,
            geoKeyOffset,
            pixelDataOffset,
            entries);
    }

    private static byte[] BigTiffWithOverflowingIfdOffset()
    {
        var bytes = new byte[16];
        bytes[0] = 0x49;
        bytes[1] = 0x49;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 43);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), ulong.MaxValue - 4);
        return bytes;
    }

    private static int WriteEntry(
        byte[] bytes,
        Dictionary<ushort, int> entryOffsets,
        int entryOffset,
        ushort tag,
        ushort type,
        ulong count,
        ulong value,
        bool inline,
        TiffContainer container,
        TiffByteOrder byteOrder)
    {
        entryOffsets[tag] = entryOffset;
        WriteUInt16(bytes, entryOffset, tag, byteOrder);
        WriteUInt16(bytes, entryOffset + 2, type, byteOrder);
        WriteCount(bytes, entryOffset + 4, count, container, byteOrder);
        var valueFieldOffset = entryOffset + (container == TiffContainer.Classic ? 8 : 12);
        if (inline)
        {
            WriteInlineUnsigned(bytes, valueFieldOffset, value, type, byteOrder);
        }
        else
        {
            WritePointer(bytes, valueFieldOffset, value, container, byteOrder);
        }

        return entryOffset + (container == TiffContainer.Classic ? 12 : 20);
    }

    private static void WriteHeader(
        byte[] bytes,
        TiffContainer container,
        TiffByteOrder byteOrder,
        int ifdOffset)
    {
        var marker = byteOrder == TiffByteOrder.LittleEndian ? (byte)0x49 : (byte)0x4d;
        bytes[0] = marker;
        bytes[1] = marker;
        WriteUInt16(bytes, 2, container == TiffContainer.Classic ? (ushort)42 : (ushort)43, byteOrder);
        if (container == TiffContainer.Classic)
        {
            WriteUInt32(bytes, 4, (uint)ifdOffset, byteOrder);
        }
        else
        {
            WriteUInt16(bytes, 4, 8, byteOrder);
            WriteUInt16(bytes, 6, 0, byteOrder);
            WriteUInt64(bytes, 8, (ulong)ifdOffset, byteOrder);
        }
    }

    private static void WriteIfdEntryCount(
        byte[] bytes,
        int offset,
        ulong count,
        TiffContainer container,
        TiffByteOrder byteOrder)
    {
        if (container == TiffContainer.Classic)
        {
            WriteUInt16(bytes, offset, (ushort)count, byteOrder);
        }
        else
        {
            WriteUInt64(bytes, offset, count, byteOrder);
        }
    }

    private static int ValueFieldOffset(TiffLayout layout, ushort tag)
        => layout.EntryOffsets[tag] + (layout.Container == TiffContainer.Classic ? 8 : 12);

    private static void WriteCount(
        byte[] bytes,
        int offset,
        ulong count,
        TiffContainer container,
        TiffByteOrder byteOrder)
    {
        if (container == TiffContainer.Classic)
        {
            WriteUInt32(bytes, offset, (uint)count, byteOrder);
        }
        else
        {
            WriteUInt64(bytes, offset, count, byteOrder);
        }
    }

    private static void WritePointer(
        byte[] bytes,
        int offset,
        ulong pointer,
        TiffContainer container,
        TiffByteOrder byteOrder)
    {
        if (container == TiffContainer.Classic)
        {
            WriteUInt32(bytes, offset, (uint)pointer, byteOrder);
        }
        else
        {
            WriteUInt64(bytes, offset, pointer, byteOrder);
        }
    }

    private static void WriteInlineUnsigned(
        byte[] bytes,
        int offset,
        ulong value,
        ushort type,
        TiffByteOrder byteOrder)
    {
        switch (type)
        {
            case TiffTypeShort:
                WriteUInt16(bytes, offset, (ushort)value, byteOrder);
                break;
            case TiffTypeLong:
                WriteUInt32(bytes, offset, (uint)value, byteOrder);
                break;
            default:
                WriteUInt64(bytes, offset, value, byteOrder);
                break;
        }
    }

    private static void WriteGeoKeyDirectory(
        byte[] bytes,
        int offset,
        ushort rasterType,
        ushort epsg,
        TiffByteOrder byteOrder)
    {
        WriteUInt16(bytes, offset, 1, byteOrder);
        WriteUInt16(bytes, offset + 2, 1, byteOrder);
        WriteUInt16(bytes, offset + 4, 0, byteOrder);
        WriteUInt16(bytes, offset + 6, 2, byteOrder);
        WriteGeoKey(bytes, offset + 8, 1025, rasterType, byteOrder);
        var crsKey = epsg == 4326 ? (ushort)2048 : (ushort)3072;
        WriteGeoKey(bytes, offset + 16, crsKey, epsg, byteOrder);
    }

    private static void WriteGeoKey(
        byte[] bytes,
        int offset,
        ushort key,
        ushort value,
        TiffByteOrder byteOrder)
    {
        WriteUInt16(bytes, offset, key, byteOrder);
        WriteUInt16(bytes, offset + 2, 0, byteOrder);
        WriteUInt16(bytes, offset + 4, 1, byteOrder);
        WriteUInt16(bytes, offset + 6, value, byteOrder);
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value, TiffByteOrder byteOrder)
    {
        if (byteOrder == TiffByteOrder.LittleEndian)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), value);
        }
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value, TiffByteOrder byteOrder)
    {
        if (byteOrder == TiffByteOrder.LittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
        }
    }

    private static void WriteUInt64(byte[] bytes, int offset, ulong value, TiffByteOrder byteOrder)
    {
        if (byteOrder == TiffByteOrder.LittleEndian)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset), value);
        }
    }

    private static void WriteDouble(byte[] bytes, int offset, double value, TiffByteOrder byteOrder)
        => WriteUInt64(bytes, offset, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)), byteOrder);

    private enum StorageShape
    {
        None,
        SingleStrip,
        MultiStripTruncated,
        TiledTruncated
    }

    private sealed record TiffLayout(
        byte[] Bytes,
        TiffContainer Container,
        TiffByteOrder ByteOrder,
        int IfdOffset,
        int NextIfdOffset,
        int ScaleOffset,
        int TiepointOffset,
        int GeoKeyOffset,
        int PixelDataOffset,
        IReadOnlyDictionary<ushort, int> EntryOffsets);
}

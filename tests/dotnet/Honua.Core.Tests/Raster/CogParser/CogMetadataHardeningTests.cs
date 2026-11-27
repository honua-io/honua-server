// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using NSubstitute;
using Xunit;

namespace Honua.Core.Tests.Raster.CogParser;

public sealed class CogMetadataHardeningTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_UserDefinedProjectedCrs32767_DoesNotFallBackToGeographicSrid()
    {
        var tiffData = BuildGeoKeyCog(new GeoKeySpec { GeographicSrid = 4326, ProjectedSrid = 32767 });
        var (reader, _) = CreateCountingReader(tiffData);

        var metadata = await new CogMetadataExtractor().ReadMetadataAsync(reader, "test-bucket", "custom-projected.tif");

        metadata.Srid.Should().Be(0, "ProjectedCSTypeGeoKey 32767 is a custom projected CRS and must not fall back to EPSG:4326");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_ProjectedModelTypeWithoutEpsg_DoesNotFallBackToGeographicSrid()
    {
        var tiffData = BuildGeoKeyCog(new GeoKeySpec { ModelType = 1, GeographicSrid = 4326 });
        var (reader, _) = CreateCountingReader(tiffData);

        var metadata = await new CogMetadataExtractor().ReadMetadataAsync(reader, "test-bucket", "projected-model.tif");

        metadata.Srid.Should().Be(0, "GTModelTypeGeoKey 1 declares a projected model and must not fall back to GeographicTypeGeoKey");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_InternalMaskIfds_AreExcludedFromOverviewLevels()
    {
        var tiffData = BuildCogWithMaskIfds();
        var (reader, _) = CreateCountingReader(tiffData);

        var metadata = await new CogMetadataExtractor().ReadMetadataAsync(reader, "test-bucket", "masked.tif");

        metadata.Width.Should().Be(512);
        metadata.Height.Should().Be(512);
        metadata.BandCount.Should().Be(3);
        metadata.OverviewLevels.Should().HaveCount(2);
        metadata.OverviewLevels.Select(l => (l.Level, l.Width, l.Height))
            .Should().Equal((0, 512, 512), (1, 256, 256));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_CogWithin64KiBPrefetch_UsesSingleRangeRequest()
    {
        var tiffData = BuildGeoKeyCog(new GeoKeySpec { ModelType = 2, GeographicSrid = 4326 });
        var (reader, getReadCount) = CreateCountingReader(tiffData);

        var metadata = await new CogMetadataExtractor().ReadMetadataAsync(reader, "test-bucket", "prefetch.tif");

        metadata.Width.Should().Be(256);
        metadata.Srid.Should().Be(4326);
        getReadCount().Should().Be(1, "external tags inside the 64 KiB header prefetch must be served from memory");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void DecodeToRgba8888_InvertedGrayscaleAndNoData_MatchesDisplaySemantics()
    {
        var metadata = new CogMetadata(
            Width: 2, Height: 1, BandCount: 1, PixelType: "uint8", Srid: 3857, Compression: "NONE",
            TileWidth: 2, TileHeight: 1, OverviewLevels: [],
            Extent: new RasterExtent { XMin = 0, YMin = 0, XMax = 2, YMax = 1 },
            PhotometricInterpretation: 0, NoData: "0");

        var rgba = CogTileEncoder.DecodeToRgba8888([0, 200], metadata);

        CogTileEncoder.CanEncodeDisplaySamples(metadata).Should().BeTrue();
        rgba.Should().NotBeNull();
        rgba![0..4].Should().Equal(0, 0, 0, 0);
        rgba![4..8].Should().Equal(55, 55, 55, 255);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void DecodeToRgba8888_BigEndianUint16Rgb_PreservesChannelOrderAndNoData()
    {
        var metadata = new CogMetadata(
            Width: 2, Height: 1, BandCount: 3, PixelType: "uint16", Srid: 3857, Compression: "NONE",
            TileWidth: 2, TileHeight: 1, OverviewLevels: [],
            Extent: new RasterExtent { XMin = 0, YMin = 0, XMax = 2, YMax = 1 },
            BitsPerSample: 16, PhotometricInterpretation: 2, NoData: "0", IsLittleEndian: false);
        var raw = new byte[2 * 3 * 2];
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(6, 2), 65535);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(8, 2), 32768);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(10, 2), 0);

        var rgba = CogTileEncoder.DecodeToRgba8888(raw, metadata);

        rgba.Should().NotBeNull();
        rgba![0..4].Should().Equal(0, 0, 0, 0);
        rgba![4..8].Should().Equal(255, 128, 0, 255);
    }

    private static (ICloudRangeReader Reader, Func<int> GetReadCount) CreateCountingReader(byte[] data)
    {
        var readCount = 0;
        var reader = Substitute.For<ICloudRangeReader>();
        reader.Provider.Returns(CloudStorageProvider.AwsS3);
        reader.GetObjectSizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((long)data.Length);
        reader.ReadRangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                readCount++;
                var offset = (int)call.ArgAt<long>(2);
                var length = Math.Min(call.ArgAt<int>(3), Math.Max(0, data.Length - offset));
                return data.AsSpan(offset, length).ToArray();
            });
        return (reader, () => readCount);
    }

    private static byte[] BuildGeoKeyCog(GeoKeySpec spec)
    {
        var keys = CollectGeoKeys(spec);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write((uint)8);
        writer.Write((ushort)8);

        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 256, Type = 4, Count = 1, Value = 256 });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 257, Type = 4, Count = 1, Value = 256 });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 259, Type = 3, Count = 1, Value = 1 });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 322, Type = 4, Count = 1, Value = 256 });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 323, Type = 4, Count = 1, Value = 256 });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 324, Type = 4, Count = 1, Value = 5000 });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 325, Type = 4, Count = 1, Value = 1000 });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 34735, Type = 3, Count = (uint)((keys.Count + 1) * 4), Value = 110 });
        writer.Write((uint)0);

        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)keys.Count);
        foreach (var (id, val) in keys)
        {
            writer.Write(id);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write(val);
        }

        return ms.ToArray();
    }

    private static List<(ushort Id, ushort Value)> CollectGeoKeys(GeoKeySpec spec)
    {
        var keys = new List<(ushort Id, ushort Value)>();
        if (spec.ModelType is { } model)
        {
            keys.Add((1024, model));
        }

        if (spec.GeographicSrid is { } geo)
        {
            keys.Add((2048, geo));
        }

        if (spec.ProjectedSrid is { } proj)
        {
            keys.Add((3072, proj));
        }

        return keys;
    }

    private static byte[] BuildCogWithMaskIfds()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        const uint firstIfd = 8;
        const uint ifdSize = 2 + (9 * 12) + 4;
        writer.Write(firstIfd);

        WriteIfd(writer, new IfdSpec { SubfileType = 0, Width = 512, Height = 512, Bands = 3, NextIfd = firstIfd + ifdSize });
        WriteIfd(writer, new IfdSpec { SubfileType = 4, Width = 512, Height = 512, Bands = 1, NextIfd = firstIfd + (2 * ifdSize) });
        WriteIfd(writer, new IfdSpec { SubfileType = 1, Width = 256, Height = 256, Bands = 3, NextIfd = firstIfd + (3 * ifdSize) });
        WriteIfd(writer, new IfdSpec { SubfileType = 5, Width = 256, Height = 256, Bands = 1, NextIfd = 0 });
        return stream.ToArray();
    }

    private static void WriteIfd(BinaryWriter writer, IfdSpec spec)
    {
        writer.Write((ushort)9);
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 254, Type = 4, Count = 1, Value = spec.SubfileType });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 256, Type = 4, Count = 1, Value = spec.Width });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 257, Type = 4, Count = 1, Value = spec.Height });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 259, Type = 3, Count = 1, Value = 1 });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 277, Type = 3, Count = 1, Value = spec.Bands });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 322, Type = 4, Count = 1, Value = 256 });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 323, Type = 4, Count = 1, Value = 256 });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 324, Type = 4, Count = 1, Value = 5000 });
        WriteIfdEntry(writer, new IfdEntrySpec { Tag = 325, Type = 4, Count = 1, Value = 1000 });
        writer.Write(spec.NextIfd);
    }

    private static void WriteIfdEntry(BinaryWriter writer, IfdEntrySpec entry)
    {
        writer.Write(entry.Tag);
        writer.Write(entry.Type);
        writer.Write(entry.Count);
        writer.Write(entry.Value);
    }

    private readonly record struct GeoKeySpec
    {
        public ushort? ModelType { get; init; }

        public ushort? GeographicSrid { get; init; }

        public ushort? ProjectedSrid { get; init; }
    }

    private readonly record struct IfdSpec
    {
        public required uint SubfileType { get; init; }

        public required uint Width { get; init; }

        public required uint Height { get; init; }

        public required uint Bands { get; init; }

        public required uint NextIfd { get; init; }
    }

    private readonly record struct IfdEntrySpec
    {
        public required ushort Tag { get; init; }

        public required ushort Type { get; init; }

        public required uint Count { get; init; }

        public required uint Value { get; init; }
    }
}

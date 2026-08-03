// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Xunit;

namespace Honua.Core.Tests.Raster.CogParser;

public class CogDirectReadSupportMatrixTests
{
    private static readonly string FixtureDirectory =
        Path.Join(AppContext.BaseDirectory, "Raster", "CogParser", "Fixtures");

    public static TheoryData<string> GdalFixtures() => new()
    {
        "lzw_pred1_uint8",
        "lzw_pred2_uint8",
        "lzw_pred1_uint16",
        "lzw_pred2_uint16",
        "lzw_pred2_rgb_uint8",
        "lzw_pred2_uint8_multitile",
        "zstd_pred1_uint8",
        "zstd_pred2_uint16",
        "deflate_pred1_uint8",
        "none_uint8",
    };

    [Fact]
    public void Entries_DescribeEveryAdmissionAxisAndFallbackClass()
    {
        CogDirectReadSupportMatrix.Entries.Select(entry => entry.Axis)
            .Should().Contain(["container", "layout", "crs", "encoding", "decode"]);
        CogDirectReadSupportMatrix.Entries.Should().Contain(entry => entry.Supported);
        CogDirectReadSupportMatrix.Entries.Should().Contain(entry => !entry.Supported);
        CogDirectReadSupportMatrix.Entries.Should().Contain(entry =>
            entry.Fallback == CogDirectReadDisposition.NeedsPostgisMaterialization);
        CogDirectReadSupportMatrix.Entries.Should().Contain(entry =>
            entry.Fallback == CogDirectReadDisposition.NeedsDurableGdal);
    }

    [Theory]
    [MemberData(nameof(GdalFixtures))]
    public async Task EvaluateSource_GdalProducedLosslessFixture_IsAdmittedForRawOnly(string fixture)
    {
        var tiff = await File.ReadAllBytesAsync(Path.Join(FixtureDirectory, fixture + ".tif"));
        var reader = new InMemoryRangeReader(tiff);
        var metadata = await new CogMetadataExtractor().ReadMetadataAsync(reader, "fixtures", fixture + ".tif");

        metadata.IsBigTiff.Should().BeFalse();
        metadata.IsLittleEndian.Should().BeTrue();
        metadata.PlanarConfiguration.Should().Be(TiffConstants.PlanarConfigurationContiguous);
        metadata.Orientation.Should().Be(TiffConstants.OrientationTopLeft);
        metadata.PhotometricInterpretation.Should().Be(metadata.BandCount == 3
            ? TiffConstants.PhotometricRgb
            : TiffConstants.PhotometricBlackIsZero);
        metadata.HasModelTransformation.Should().BeFalse();
        metadata.HasSubIfds.Should().BeFalse();
        metadata.HasHeterogeneousOverviewLayout.Should().BeFalse();

        CogDirectReadSupportMatrix.EvaluateSource(metadata, RasterFormat.Raw).IsDirect.Should().BeTrue();

        var rendered = CogDirectReadSupportMatrix.EvaluateSource(metadata, RasterFormat.PNG);
        rendered.Disposition.Should().Be(CogDirectReadDisposition.NeedsPostgisMaterialization);
        rendered.Reason.Should().Be(CogDirectReadReason.UnsupportedOutputEncoding);
    }

    [Fact]
    public void PlanTile_AlignedManagedRawTile_ReturnsExactRangeAndDecodeContract()
    {
        var metadata = CreateSupportedMetadata();

        var plan = CogDirectReadSupportMatrix.PlanTile(
            metadata,
            metadata.OverviewLevels[0],
            level: 0,
            row: 0,
            col: 0,
            RasterFormat.Raw);

        plan.IsDirect.Should().BeTrue();
        plan.TileIndex.Should().Be(0);
        plan.ExpectedDecodedBytes.Should().Be(256 * 256);
        plan.ExpectedContentType.Should().Be("application/octet-stream");
    }

    [Theory]
    [InlineData("bigtiff", CogDirectReadReason.UnsupportedContainer)]
    [InlineData("big-endian", CogDirectReadReason.UnsupportedByteOrder)]
    [InlineData("planar-separate", CogDirectReadReason.UnsupportedLayout)]
    [InlineData("rotated", CogDirectReadReason.UnsupportedLayout)]
    [InlineData("subifd", CogDirectReadReason.UnsupportedLayout)]
    [InlineData("heterogeneous", CogDirectReadReason.UnsupportedLayout)]
    [InlineData("codec-layout", CogDirectReadReason.UnsupportedCodecLayout)]
    public void EvaluateSource_UnprovenSource_RequiresDurableGdal(string caseName, CogDirectReadReason expectedReason)
    {
        var metadata = caseName switch
        {
            "bigtiff" => CreateSupportedMetadata() with { IsBigTiff = true },
            "big-endian" => CreateSupportedMetadata() with { IsLittleEndian = false },
            "planar-separate" => CreateSupportedMetadata() with { PlanarConfiguration = 2 },
            "rotated" => CreateSupportedMetadata() with { HasModelTransformation = true },
            "subifd" => CreateSupportedMetadata() with { HasSubIfds = true },
            "heterogeneous" => CreateSupportedMetadata() with { HasHeterogeneousOverviewLayout = true },
            "codec-layout" => CreateSupportedMetadata() with { Compression = "LERC" },
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };

        var plan = CogDirectReadSupportMatrix.EvaluateSource(metadata, RasterFormat.Raw);

        plan.Disposition.Should().Be(CogDirectReadDisposition.NeedsDurableGdal);
        plan.Reason.Should().Be(expectedReason);
    }

    [Fact]
    public void PlanTile_NonWebMercatorSource_RequiresPostgisMaterialization()
    {
        var metadata = CreateSupportedMetadata() with { Srid = 4326 };

        var plan = CogDirectReadSupportMatrix.PlanTile(
            metadata,
            metadata.OverviewLevels[0],
            0,
            0,
            0,
            RasterFormat.Raw);

        plan.Disposition.Should().Be(CogDirectReadDisposition.NeedsPostgisMaterialization);
        plan.Reason.Should().Be(CogDirectReadReason.UnsupportedCrs);
    }

    [Fact]
    public void PlanTile_MisalignedSource_RequiresPostgisMaterialization()
    {
        var metadata = CreateSupportedMetadata() with
        {
            Extent = new RasterExtent { XMin = 0, YMin = 0, XMax = 256, YMax = 256, Srid = 3857 },
        };

        var plan = CogDirectReadSupportMatrix.PlanTile(
            metadata,
            metadata.OverviewLevels[0],
            0,
            0,
            0,
            RasterFormat.Raw);

        plan.Disposition.Should().Be(CogDirectReadDisposition.NeedsPostgisMaterialization);
        plan.Reason.Should().Be(CogDirectReadReason.MisalignedGrid);
    }

    [Fact]
    public void PlanTile_IncompleteTileInventory_IsCorrupt()
    {
        var metadata = CreateSupportedMetadata();
        var overview = metadata.OverviewLevels[0] with { TileOffsets = [], TileByteCounts = [] };

        var plan = CogDirectReadSupportMatrix.PlanTile(metadata, overview, 0, 0, 0, RasterFormat.Raw);

        plan.Disposition.Should().Be(CogDirectReadDisposition.Corrupt);
        plan.Reason.Should().Be(CogDirectReadReason.InvalidTileInventory);
    }

    [Fact]
    public void PlanTile_OversizedEncodedTile_RequiresDurableGdalWithoutRangeRead()
    {
        var metadata = CreateSupportedMetadata();
        var overview = metadata.OverviewLevels[0] with { TileByteCounts = [int.MaxValue] };

        var plan = CogDirectReadSupportMatrix.PlanTile(metadata, overview, 0, 0, 0, RasterFormat.Raw);

        plan.Disposition.Should().Be(CogDirectReadDisposition.NeedsDurableGdal);
        plan.Reason.Should().Be(CogDirectReadReason.EncodedTileTooLarge);
    }

    [Fact]
    public void PlanTile_NoneTileWithWrongStoredLength_IsCorruptBeforeRangeRead()
    {
        var metadata = CreateSupportedMetadata();
        var overview = metadata.OverviewLevels[0] with { TileByteCounts = [(256 * 256) - 1] };

        var plan = CogDirectReadSupportMatrix.PlanTile(metadata, overview, 0, 0, 0, RasterFormat.Raw);

        plan.Disposition.Should().Be(CogDirectReadDisposition.Corrupt);
        plan.Reason.Should().Be(CogDirectReadReason.InvalidPayload);
    }

    [Fact]
    public void PlanTile_RequestOutsideSource_ReturnsNoCoverage()
    {
        var metadata = CreateSupportedMetadata() with
        {
            Extent = new RasterExtent
            {
                XMin = -SpatialConstants.WebMercatorExtent,
                YMin = 0,
                XMax = 0,
                YMax = SpatialConstants.WebMercatorExtent,
                Srid = 3857,
            },
        };

        var plan = CogDirectReadSupportMatrix.PlanTile(
            metadata,
            metadata.OverviewLevels[0],
            level: 1,
            row: 1,
            col: 1,
            RasterFormat.Raw);

        plan.Disposition.Should().Be(CogDirectReadDisposition.NoCoverage);
        plan.Reason.Should().Be(CogDirectReadReason.OutsideCoverage);
    }

    [Fact]
    public void ValidatePayload_RawTileRequiresExactDecodedLength()
    {
        var metadata = CreateSupportedMetadata();
        var plan = CogDirectReadSupportMatrix.PlanTile(
            metadata,
            metadata.OverviewLevels[0],
            0,
            0,
            0,
            RasterFormat.Raw);

        CogDirectReadSupportMatrix.ValidatePayload(
            plan,
            new byte[256 * 256],
            "application/octet-stream").IsDirect.Should().BeTrue();

        var invalid = CogDirectReadSupportMatrix.ValidatePayload(
            plan,
            new byte[(256 * 256) - 1],
            "application/octet-stream");
        invalid.Disposition.Should().Be(CogDirectReadDisposition.Corrupt);
        invalid.Reason.Should().Be(CogDirectReadReason.InvalidPayload);
    }

    [Fact]
    public void ValidatePayload_JpegRequiresStandaloneCodestream()
    {
        var metadata = CreateSupportedMetadata() with
        {
            Width = 1,
            Height = 1,
            BandCount = 3,
            Compression = "JPEG",
            TileWidth = 1,
            TileHeight = 1,
            OverviewLevels = [new CogOverviewLevel(0, 1, 1, 8, [16], [CreateStandaloneJpeg().Length])],
            PhotometricInterpretation = TiffConstants.PhotometricRgb,
        };
        var plan = CogDirectReadSupportMatrix.PlanTile(
            metadata,
            metadata.OverviewLevels[0],
            0,
            0,
            0,
            RasterFormat.JPEG);

        var decodedSamples = CogDirectReadSupportMatrix.EvaluateSource(metadata, RasterFormat.Raw);
        decodedSamples.Disposition.Should().Be(CogDirectReadDisposition.NeedsPostgisMaterialization);
        decodedSamples.Reason.Should().Be(CogDirectReadReason.UnsupportedOutputEncoding);

        CogDirectReadSupportMatrix.ValidatePayload(plan, CreateStandaloneJpeg(), "image/jpeg")
            .IsDirect.Should().BeTrue();

        var invalid = CogDirectReadSupportMatrix.ValidatePayload(plan, [0xFF, 0xD8, 0xFF, 0xD9], "image/jpeg");
        invalid.Disposition.Should().Be(CogDirectReadDisposition.Corrupt);
        invalid.Reason.Should().Be(CogDirectReadReason.InvalidPayload);

        var wrongDimensions = CogDirectReadSupportMatrix.ValidatePayload(
            plan with { ExpectedWidth = 256, ExpectedHeight = 256 },
            CreateStandaloneJpeg(),
            "image/jpeg");
        wrongDimensions.Disposition.Should().Be(CogDirectReadDisposition.Corrupt);
        wrongDimensions.Reason.Should().Be(CogDirectReadReason.InvalidPayload);
    }

    private static CogMetadata CreateSupportedMetadata() => new(
        Width: 256,
        Height: 256,
        BandCount: 1,
        PixelType: "uint8",
        Srid: 3857,
        Compression: "NONE",
        TileWidth: 256,
        TileHeight: 256,
        OverviewLevels:
        [
            new CogOverviewLevel(0, 256, 256, 8, [16], [256 * 256]),
        ],
        Extent: CreateWorldExtent(),
        BitsPerSample: 8,
        Predictor: 1,
        IsLittleEndian: true,
        PhotometricInterpretation: TiffConstants.PhotometricBlackIsZero);

    private static RasterExtent CreateWorldExtent() => new()
    {
        XMin = -SpatialConstants.WebMercatorExtent,
        YMin = -SpatialConstants.WebMercatorExtent,
        XMax = SpatialConstants.WebMercatorExtent,
        YMax = SpatialConstants.WebMercatorExtent,
        Srid = 3857,
    };

    private static byte[] CreateStandaloneJpeg() => Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////" +
        "2wBDAf//////////////////////////////////////////////////////////////////////////////////////" +
        "wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF/" +
        "/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAA" +
        "AAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/a" +
        "AAgBAQABPyF//9oADAMBAAIAAwAAABD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EB//xAAUEQEAAAAAAAAAAAAAAAAAAAAA" +
        "/9oACAECAQE/EB//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/EB//2Q==");

    private sealed class InMemoryRangeReader(byte[] data) : ICloudRangeReader
    {
        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public Task<byte[]> ReadRangeAsync(
            string bucket,
            string key,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            var available = Math.Max(0, data.Length - (int)offset);
            var bytesToRead = Math.Min(length, available);
            var result = new byte[bytesToRead];
            if (bytesToRead > 0)
            {
                Buffer.BlockCopy(data, (int)offset, result, 0, bytesToRead);
            }

            return Task.FromResult(result);
        }

        public Task<Stream> ReadRangeStreamAsync(
            string bucket,
            string key,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            var available = Math.Max(0, data.Length - (int)offset);
            var bytesToRead = Math.Min(length, available);
            return Task.FromResult<Stream>(new Honua.TestKit.CallerOwnedMemoryStream(data, (int)offset, bytesToRead));
        }

        public Task<long> GetObjectSizeAsync(
            string bucket,
            string key,
            CancellationToken cancellationToken = default) => Task.FromResult((long)data.Length);
    }
}

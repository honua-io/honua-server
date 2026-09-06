// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.Cog;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Cog;

[Protocol(TestProtocols.ImageServer)]
public sealed class CogEncodedTileTests
{
    [Theory]
    [InlineData("deflate_pred1_uint8", 1, 8, "area")]
    [InlineData("lzw_pred2_uint8", 1, 8, "area")]
    [InlineData("lzw_pred2_rgb_uint8", 3, 8, "area")]
    [InlineData("lzw_pred2_uint16", 1, 16, "area")]
    [InlineData("zstd_pred1_uint8", 1, 8, "area")]
    [InlineData("zstd_pred2_uint16", 1, 16, "area")]
    [InlineData("none_uint8", 1, 8, "area")]
    [InlineData("none_uint8", 1, 8, "point")]
    [InlineData("none_uint8", 1, 8, "matrix")]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_GdalCog_ReturnsImagesWithIndependentGdalSamples(string fixture, int bands, int bits, string georeferencing)
    {
        // These are GDAL-generated TIFFs with paired GDAL-decoded bytes, not Honua snapshots.
        var directory = Path.Combine(AppContext.BaseDirectory, "CogFixtures");
        var source = await File.ReadAllBytesAsync(Path.Combine(directory, fixture + ".tif"));
        if (georeferencing == "point")
        {
            SetPointGeoreferencing(source);
        }
        if (georeferencing == "matrix")
        {
            source = SetMatrixGeoreferencing(source);
        }
        var expected = await File.ReadAllBytesAsync(Path.Combine(directory, fixture + ".bin"));
        var reader = new FixtureReader(source);
        var store = Substitute.For<ICogStore>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CogTileResolver([reader], new CogMetadataExtractor(), store, cache,
            NullLogger<CogTileResolver>.Instance);
        var registration = new CogRegistration
        {
            Id = 1, LayerId = 1, Name = fixture, Provider = CloudStorageProvider.AwsS3,
            Bucket = "fixtures", ObjectKey = fixture, CreatedAt = DateTimeOffset.UnixEpoch
        };

        // Fixture scale 1222.992452562495 m/px * 128 px = the XYZ zoom-8 tile span.
        var result = await resolver.GetTileAsync(registration, 8, 0, 0, RasterFormat.PNG);
        result.Should().NotBeNull();
        result!.Value.ContentType.Should().Be("image/png");
        result.Value.Width.Should().Be(128);
        result.Value.Height.Should().Be(128);
        result.Value.Srid.Should().Be(3857);
        var chunks = ReadPngChunks(result.Value.Data);
        chunks["IHDR"][8].Should().Be((byte)bits);
        chunks["IHDR"][9].Should().Be(bands == 1 ? (byte)0 : (byte)2);
        chunks.Should().NotContainKey("tRNS"); // The fixture declares no nodata.
        var pixels = InflateSamples(chunks["IDAT"], 128 * bands * bits / 8, 128);
        if (bits == 16)
        {
            for (var i = 0; i < expected.Length; i += 2)
            {
                BinaryPrimitives.ReadUInt16BigEndian(pixels.AsSpan(i)).Should()
                    .Be(BinaryPrimitives.ReadUInt16LittleEndian(expected.AsSpan(i)));
            }
        }
        else
        {
            pixels.Should().Equal(expected);
        }
        await store.Received(1).UpdateMetadataAsync(1,
            Arg.Is<CogMetadata>(m => m.Srid == 3857 && m.BandCount == bands
                && m.BitsPerSample == bits && m.NoData == null
                && Math.Abs(m.Extent.XMin + 20037508.342789244) < 1e-7
                && Math.Abs(m.Extent.YMax - 20037508.342789244) < 1e-7),
            null, Arg.Any<CancellationToken>());

        foreach (var format in new[] { RasterFormat.TIFF, RasterFormat.COG })
        {
            var tiff = await resolver.GetTileAsync(registration, 8, 0, 0, format);
            tiff.Should().NotBeNull();
            tiff!.Value.ContentType.Should().Be("image/tiff");
            tiff.Value.Data[..8].Should().Equal(73, 73, 42, 0, 8, 0, 0, 0);
            tiff.Value.Data[^expected.Length..].Should().Equal(expected);
            // The output is independently covered at tag/value level by CogTiffTileEncoderTests.
            var outputMetadata = await new CogMetadataExtractor().ReadMetadataAsync(
                new CogPinnedRangeReader(new FixtureReader(tiff.Value.Data), "fixture-v1"), "fixtures", "output");
            outputMetadata.Width.Should().Be(128);
            outputMetadata.Height.Should().Be(128);
            outputMetadata.BitsPerSample.Should().Be(bits);
            outputMetadata.BandCount.Should().Be(bands);
            outputMetadata.Srid.Should().Be(3857);
            outputMetadata.Extent.XMin.Should().BeApproximately(-20037508.342789244, 1e-7);
            outputMetadata.Extent.YMax.Should().BeApproximately(20037508.342789244, 1e-7);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EncodePng_Uint16NoData_PreservesValuesByteOrderAndTransparency(bool littleEndian)
    {
        // Independent numeric fixture: 0, 258, 65535 (nodata), 4096.
        byte[] samples = littleEndian ? [0, 0, 2, 1, 255, 255, 0, 16] : [0, 0, 1, 2, 255, 255, 16, 0];
        var metadata = new CogMetadata(2, 2, 1, "uint16", 3857, "NONE", 2, 2, [],
            new RasterExtent { XMin = 0, YMin = 0, XMax = 2, YMax = 2 },
            BitsPerSample: 16, IsLittleEndian: littleEndian, NoData: "65535");
        var png = CogTileEncoder.EncodePng(samples, metadata);
        png.Should().NotBeNull();
        var chunks = ReadPngChunks(png!);
        chunks["tRNS"].Should().Equal(255, 255);
        InflateSamples(chunks["IDAT"], 4, 2).Should().Equal(0, 0, 1, 2, 255, 255, 16, 0);
    }

    [Fact]
    public void EncodePng_TruncatedSamples_RejectsMalformedTile()
    {
        var metadata = new CogMetadata(2, 2, 1, "uint8", 3857, "NONE", 2, 2, [],
            new RasterExtent { XMin = 0, YMin = 0, XMax = 2, YMax = 2 });
        var act = () => CogTileEncoder.EncodePng([1, 2, 3], metadata);
        act.Should().Throw<InvalidDataException>();
        CogTileEncoder.EncodePng([1, 2, 3, 4], metadata with { PlanarConfiguration = 2 }).Should().BeNull();
        CogTileEncoder.EncodePng([1, 2, 3, 4], metadata with { PhotometricInterpretation = 3 }).Should().BeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65535")]
    public async Task ReadMetadataAsync_InlineOrExternalNoData_PreservesDeclaredValue(string noData)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write(8u);
        writer.Write((ushort)5);
        foreach (var (tag, type, value) in new (ushort, ushort, uint)[]
        {
            (256, 4, 2), (257, 4, 2), (258, 3, 16), (262, 3, 1)
        })
        {
            writer.Write(tag);
            writer.Write(type);
            writer.Write(1u);
            writer.Write(value);
        }
        var ascii = Encoding.ASCII.GetBytes(noData + "\0");
        writer.Write((ushort)42113);
        writer.Write((ushort)2);
        writer.Write((uint)ascii.Length);
        if (ascii.Length <= 4)
        {
            var inline = new byte[4];
            ascii.CopyTo(inline, 0);
            writer.Write(inline);
        }
        else
        {
            writer.Write(74u); // 8-byte header + 2 + 5*12 + 4-byte next-IFD pointer.
        }
        writer.Write(0u);
        if (ascii.Length > 4)
        {
            writer.Write(ascii);
        }
        var metadata = await new CogMetadataExtractor().ReadMetadataAsync(
            new CogPinnedRangeReader(new FixtureReader(stream.ToArray()), "fixture-v1"), "fixtures", "nodata");
        metadata.NoData.Should().Be(noData);
        metadata.PixelType.Should().Be("uint16");
        metadata.PhotometricInterpretation.Should().Be(1);
        metadata.PlanarConfiguration.Should().Be(1);
    }

    private static Dictionary<string, byte[]> ReadPngChunks(byte[] png)
    {
        png[..8].Should().Equal(137, 80, 78, 71, 13, 10, 26, 10);
        var chunks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var offset = 8; offset < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            chunks.Add(type, png.AsSpan(offset + 8, length).ToArray());
            offset += 12 + length;
        }
        return chunks;
    }

    private static byte[] SetMatrixGeoreferencing(byte[] source)
    {
        // Keep GDAL's real tile and keys, replacing PixelScale with an equivalent affine matrix.
        var matrixOffset = (source.Length + 7) & ~7;
        var result = new byte[matrixOffset + 128];
        source.CopyTo(result, 0);
        const double scale = 1222.992452562495;
        double[] matrix = [scale, 0, 0, -20037508.342789244, 0, -scale, 0, 20037508.342789244,
            0, 0, 1, 0, 0, 0, 0, 1];
        for (var i = 0; i < matrix.Length; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(result.AsSpan(matrixOffset + i * 8), matrix[i]);
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(ifd));
        var entries = new List<byte[]>();
        for (var i = 0; i < count; i++)
        {
            var entry = result.AsSpan(ifd + 2 + i * 12, 12).ToArray();
            if (BinaryPrimitives.ReadUInt16LittleEndian(entry) == 33550)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(entry, 34264);
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), 16);
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), (uint)matrixOffset);
            }
            entries.Add(entry);
        }
        var ordered = entries.OrderBy(entry => BinaryPrimitives.ReadUInt16LittleEndian(entry)).ToArray();
        for (var i = 0; i < ordered.Length; i++) ordered[i].CopyTo(result, ifd + 2 + i * 12);
        return result;
    }

    private static void SetPointGeoreferencing(byte[] source)
    {
        // Re-express the same grid with raster tiepoint (3,7) at its pixel centre.
        // The TIFF samples stay untouched; expected PNG samples remain GDAL's original decode.
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(ifd));
        var foundPointKey = false;
        var foundTiepoint = false;
        for (var i = 0; i < count; i++)
        {
            var entry = ifd + 2 + i * 12;
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(entry));
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(entry + 8));
            if (tag == 34735)
            {
                var keys = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset + 6));
                for (var key = 0; key < keys; key++)
                {
                    var keyOffset = offset + 8 + key * 8;
                    if (BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(keyOffset)) == 1025)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(keyOffset + 6), 2);
                        foundPointKey = true;
                    }
                }
            }
            else if (tag == 33922)
            {
                const double scale = 1222.992452562495;
                BinaryPrimitives.WriteDoubleLittleEndian(source.AsSpan(offset), 3);
                BinaryPrimitives.WriteDoubleLittleEndian(source.AsSpan(offset + 8), 7);
                BinaryPrimitives.WriteDoubleLittleEndian(source.AsSpan(offset + 24), -20037508.342789244 + 3.5 * scale);
                BinaryPrimitives.WriteDoubleLittleEndian(source.AsSpan(offset + 32), 20037508.342789244 - 7.5 * scale);
                foundTiepoint = true;
            }
        }
        foundPointKey.Should().BeTrue();
        foundTiepoint.Should().BeTrue();
    }

    private static byte[] InflateSamples(byte[] idat, int stride, int height)
    {
        using var compressed = new MemoryStream(idat);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var samples = new byte[stride * height];
        for (var row = 0; row < height; row++)
        {
            zlib.ReadByte().Should().Be(0); // Independently undo filter None.
            zlib.ReadExactly(samples.AsSpan(row * stride, stride));
        }
        zlib.ReadByte().Should().Be(-1);
        return samples;
    }

    private sealed class FixtureReader(byte[] source) : ICloudRangeReader
    {
        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;
        public Task<CloudObjectMetadata> GetObjectMetadataAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromResult(new CloudObjectMetadata { SizeBytes = source.Length, ETag = "fixture-v1" });
        public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromResult((long)source.Length);
        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Every fixture read must be ETag pinned.");
        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, string expectedETag,
            CancellationToken cancellationToken = default)
        {
            expectedETag.Should().Be("fixture-v1");
            return Task.FromResult(source.AsSpan((int)offset, Math.Min(length, source.Length - (int)offset)).ToArray());
        }
        public Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

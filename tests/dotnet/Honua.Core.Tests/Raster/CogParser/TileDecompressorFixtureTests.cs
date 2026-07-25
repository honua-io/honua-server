// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Xunit;

namespace Honua.Core.Tests.Raster.CogParser;

/// <summary>
/// Cross-checks the tile decode path against GDAL.
///
/// Every fixture under <c>Fixtures/</c> was produced by GDAL 3.12.1 (via rasterio 1.5.0);
/// the paired <c>.bin</c> holds GDAL's own decode of that file, concatenated in TIFF tile
/// order. Decoding the <c>.tif</c> must reproduce those bytes exactly, so these tests
/// validate against what real tooling emits rather than against a Honua-side encoder.
/// Regenerate with <c>scripts/raster/generate-cog-fixtures.py</c>.
/// </summary>
public class TileDecompressorFixtureTests
{
    private static readonly string FixtureDirectory =
        Path.Join(AppContext.BaseDirectory, "Raster", "CogParser", "Fixtures");

    public static TheoryData<string, string, int, int, int> GdalFixtures() => new()
    {
        // name, compression, predictor, bands, bitsPerSample
        { "lzw_pred1_uint8", "LZW", 1, 1, 8 },
        { "lzw_pred2_uint8", "LZW", 2, 1, 8 },
        { "lzw_pred1_uint16", "LZW", 1, 1, 16 },
        { "lzw_pred2_uint16", "LZW", 2, 1, 16 },
        { "lzw_pred2_rgb_uint8", "LZW", 2, 3, 8 },
        { "lzw_pred2_uint8_multitile", "LZW", 2, 1, 8 },
        { "zstd_pred1_uint8", "ZSTD", 1, 1, 8 },
        { "zstd_pred2_uint16", "ZSTD", 2, 1, 16 },
        { "deflate_pred1_uint8", "DEFLATE", 1, 1, 8 },
        { "none_uint8", "NONE", 1, 1, 8 },
    };

    [Theory]
    [MemberData(nameof(GdalFixtures))]
    public async Task Decompress_GdalProducedFixture_ReproducesGdalDecodedPixels(
        string fixture, string compression, int predictor, int bands, int bitsPerSample)
    {
        var (metadata, reader) = await ReadFixtureMetadataAsync(fixture);

        metadata.Compression.Should().Be(compression);
        metadata.Predictor.Should().Be(predictor);
        metadata.BandCount.Should().Be(bands);
        metadata.BitsPerSample.Should().Be(bitsPerSample);

        var actual = await DecodeAllTilesAsync(metadata, reader);
        var expected = await File.ReadAllBytesAsync(Path.Join(FixtureDirectory, fixture + ".bin"));

        AssertBytesEqual(expected, actual, fixture);
    }

    [Fact]
    public async Task Decompress_LzwWithPredictorButLayoutOmittingIt_ProducesWrongPixels()
    {
        // Guards the guard: proves the fixture comparison would actually catch a decoder that
        // ignored tag 317, rather than the predictor being a no-op on this data.
        var (metadata, reader) = await ReadFixtureMetadataAsync("lzw_pred2_uint8");
        var expected = await File.ReadAllBytesAsync(Path.Join(FixtureDirectory, "lzw_pred2_uint8.bin"));

        var withoutPredictor = await DecodeAllTilesAsync(metadata, reader, TilePixelLayout.None);

        withoutPredictor.Should().HaveCount(expected.Length);
        withoutPredictor.Should().NotEqual(expected);
    }

    [Fact]
    public async Task Decompress_SameRasterAcrossCodecs_ReturnsIdenticalPixels()
    {
        // lzw/zstd/deflate/none uint8 fixtures were written from one source array, so the
        // codecs must agree byte-for-byte with each other as well as with GDAL.
        var lzw = await DecodeFixtureAsync("lzw_pred1_uint8");
        var lzwPredicted = await DecodeFixtureAsync("lzw_pred2_uint8");
        var zstd = await DecodeFixtureAsync("zstd_pred1_uint8");
        var deflate = await DecodeFixtureAsync("deflate_pred1_uint8");
        var none = await DecodeFixtureAsync("none_uint8");

        AssertBytesEqual(none, lzw, "lzw_pred1_uint8 vs none_uint8");
        AssertBytesEqual(none, lzwPredicted, "lzw_pred2_uint8 vs none_uint8");
        AssertBytesEqual(none, zstd, "zstd_pred1_uint8 vs none_uint8");
        AssertBytesEqual(none, deflate, "deflate_pred1_uint8 vs none_uint8");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(4096)]
    public void Decompress_CorruptLzwInput_TerminatesWithoutUnhandledFailure(int seed)
    {
        // Tile bytes are attacker-controlled. Malformed LZW must fail as InvalidDataException
        // (or decode to something harmless) rather than hang, overrun the code table, or
        // surface an unhandled exception type from deep in the decoder.
        var random = new Random(seed);
        var garbage = new byte[2048];
        random.NextBytes(garbage);

        var act = () => TileDecompressor.Decompress(garbage, "LZW", TilePixelLayout.None, maxDecompressedBytes: 1 << 20);

        act.Should().NotThrow<IndexOutOfRangeException>();
        act.Should().NotThrow<ArgumentException>();
        act.Should().NotThrow<OutOfMemoryException>();
    }

    [Fact]
    public void Decompress_LzwCodeReferencingUndefinedEntry_ThrowsInvalidData()
    {
        // 9-bit MSB-first codes: Clear, a root literal, then 400 — past the next free
        // entry (258), so it names a table slot the encoder never defined.
        var stream = PackNineBitCodes(256, 65, 400);

        var act = () => TileDecompressor.Decompress(stream, "LZW");

        act.Should().Throw<InvalidDataException>().WithMessage("*before it is defined*");
    }

    [Fact]
    public void Decompress_LzwNonLiteralAfterClear_ThrowsInvalidData()
    {
        // A freshly cleared table holds only roots, so the next code must be a literal.
        var stream = PackNineBitCodes(256, 400);

        var act = () => TileDecompressor.Decompress(stream, "LZW");

        act.Should().Throw<InvalidDataException>().WithMessage("*not a literal*");
    }

    private static byte[] PackNineBitCodes(params int[] codes)
    {
        var bits = new List<bool>();
        foreach (var code in codes)
        {
            for (var bit = 8; bit >= 0; bit--)
            {
                bits.Add(((code >> bit) & 1) != 0);
            }
        }

        var packed = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                packed[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return packed;
    }

    [Fact]
    public void Decompress_ZstdFrameWithoutContentSize_DecodesToExactLength()
    {
        // GDAL always writes the frame content size, so the fixtures only cover the sized path.
        // A streaming-produced frame omits it and the header size estimate over-reports, which
        // must be trimmed back to the bytes actually written rather than returned padded.
        var raw = new byte[4096];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)((i * 31) ^ (i >> 3));
        }

        using var buffer = new MemoryStream();
        using (var compressionStream = new ZstdSharp.CompressionStream(buffer))
        {
            compressionStream.Write(raw, 0, raw.Length);
        }

        var framed = buffer.ToArray();
        ZstdSharp.Decompressor.GetDecompressedSize(framed).Should().NotBe((ulong)raw.Length,
            "this test is only meaningful while the frame omits its content size");

        var (decoded, _) = TileDecompressor.Decompress(framed, "ZSTD");

        decoded.Should().Equal(raw);
    }

    [Fact]
    public void Decompress_ZstdFrameDeclaringOversizedContent_ThrowsBeforeAllocating()
    {
        // The declared size is attacker-controlled file content; it must be bounds-checked
        // against the ceiling rather than trusted as an allocation size.
        var raw = new byte[64 * 1024];
        using var compressor = new ZstdSharp.Compressor();
        var framed = compressor.Wrap(raw).ToArray();

        var act = () => TileDecompressor.Decompress(framed, "ZSTD", maxDecompressedBytes: 1024);

        act.Should().Throw<InvalidDataException>().WithMessage("*decompression bomb*");
    }

    [Fact]
    public void Decompress_UnsupportedCodec_ThrowsNamingCodecAndSupportedSet()
    {
        var act = () => TileDecompressor.Decompress([1, 2, 3], "LERC");

        var exception = act.Should().Throw<UnsupportedTileCodecException>().Which;
        exception.Codec.Should().Be("LERC");
        exception.SupportedCodecs.Should().Contain(["LZW", "ZSTD", "DEFLATE"]);
        exception.Message.Should().Contain("LERC");
    }

    [Fact]
    public void Decompress_FloatingPointPredictor_ThrowsNamingPredictor()
    {
        var layout = new TilePixelLayout(
            TileWidth: 4,
            SamplesPerPixel: 1,
            BitsPerSample: 32,
            Predictor: TilePixelLayout.PredictorFloatingPoint,
            IsLittleEndian: true);

        var act = () => TileDecompressor.Decompress(new byte[16], "NONE", layout);

        var exception = act.Should().Throw<UnsupportedTilePredictorException>().Which;
        exception.Predictor.Should().Be(3);
        exception.Message.Should().Contain("317");
    }

    [Fact]
    public void Decompress_NoneWithPredictor_DoesNotMutateCallerBuffer()
    {
        var tile = new byte[] { 10, 5, 5, 5 };
        var layout = new TilePixelLayout(4, 1, 8, TilePixelLayout.PredictorHorizontalDifferencing, true);

        var (decoded, _) = TileDecompressor.Decompress(tile, "NONE", layout);

        decoded.Should().Equal(10, 15, 20, 25);
        tile.Should().Equal(10, 5, 5, 5);
    }

    [Fact]
    public void IsSupported_LzwAndZstd_ReturnsTrue()
    {
        TileDecompressor.IsSupported("LZW").Should().BeTrue();
        TileDecompressor.IsSupported("ZSTD").Should().BeTrue();
        TileDecompressor.IsSupported("LERC").Should().BeFalse();
    }

    private static async Task<(CogMetadata Metadata, InMemoryRangeReader Reader)>
        ReadFixtureMetadataAsync(string fixture)
    {
        var tiff = await File.ReadAllBytesAsync(Path.Join(FixtureDirectory, fixture + ".tif"));
        var reader = new InMemoryRangeReader(tiff);
        var metadata = await new CogMetadataExtractor().ReadMetadataAsync(reader, "fixtures", fixture + ".tif");
        return (metadata, reader);
    }

    private static async Task<byte[]> DecodeFixtureAsync(string fixture)
    {
        var (metadata, reader) = await ReadFixtureMetadataAsync(fixture);
        return await DecodeAllTilesAsync(metadata, reader);
    }

    private static async Task<byte[]> DecodeAllTilesAsync(
        CogMetadata metadata,
        InMemoryRangeReader reader,
        TilePixelLayout? layoutOverride = null)
    {
        var layout = layoutOverride ?? new TilePixelLayout(
            metadata.TileWidth,
            metadata.BandCount,
            metadata.BitsPerSample,
            metadata.Predictor,
            metadata.IsLittleEndian);

        var level = metadata.OverviewLevels[0];
        var decoded = new List<byte>();

        for (var i = 0; i < level.TileOffsets.Length; i++)
        {
            var tileBytes = await reader.ReadRangeAsync(
                "fixtures", "tile", level.TileOffsets[i], level.TileByteCounts[i]);
            var (data, contentType) = TileDecompressor.Decompress(tileBytes, metadata.Compression, layout);
            contentType.Should().Be("application/octet-stream");
            decoded.AddRange(data);
        }

        return decoded.ToArray();
    }

    private static void AssertBytesEqual(byte[] expected, byte[] actual, string because)
    {
        actual.Length.Should().Be(expected.Length, "decoded length must match GDAL for {0}", because);

        // Report the first divergence rather than dumping tens of kilobytes of pixels.
        var index = expected.AsSpan().CommonPrefixLength(actual);
        if (index != expected.Length)
        {
            Assert.Fail(
                $"{because}: decoded pixels diverge from GDAL at byte {index} " +
                $"(expected 0x{expected[index]:X2}, got 0x{actual[index]:X2}).");
        }
    }

    private sealed class InMemoryRangeReader : ICloudRangeReader
    {
        private readonly byte[] _data;

        public InMemoryRangeReader(byte[] data) => _data = data;

        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            var available = Math.Max(0, _data.Length - (int)offset);
            var bytesToRead = Math.Min(length, available);
            var result = new byte[bytesToRead];
            if (bytesToRead > 0)
            {
                Buffer.BlockCopy(_data, (int)offset, result, 0, bytesToRead);
            }
            return Task.FromResult(result);
        }

        public Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            var available = Math.Max(0, _data.Length - (int)offset);
            var bytesToRead = Math.Min(length, available);
            return Task.FromResult<Stream>(new Honua.TestKit.CallerOwnedMemoryStream(_data, (int)offset, bytesToRead));
        }
        public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromResult((long)_data.Length);
    }
}

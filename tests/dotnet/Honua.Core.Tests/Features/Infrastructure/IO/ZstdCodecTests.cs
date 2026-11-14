// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using Honua.Core.Features.Infrastructure.IO;

namespace Honua.Core.Tests.Features.Infrastructure.IO;

public sealed class ZstdCodecTests
{
    private static byte[] Sample(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 31) ^ (i >> 3));
        }

        return data;
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.SmallestSize)]
    public void Compress_ThenDecompress_RoundTripsExactBytes(CompressionLevel level)
    {
        var raw = Sample(70_000);

        var framed = ZstdCodec.Compress(raw, level);

        ZstdCodec.GetDecompressedBound(framed).Should().Be((ulong)raw.Length, "a one-shot frame declares its size");
        ZstdCodec.Decompress(framed, raw.Length).Should().Equal(raw);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void CreateCompressionStream_LeavesOutputOpenAndRoundTrips()
    {
        var raw = Sample(10_000);
        using var output = new MemoryStream();
        using (var stream = ZstdCodec.CreateCompressionStream(output, CompressionLevel.Fastest))
        {
            stream.Write(raw);
        }

        output.CanWrite.Should().BeTrue("the caller owns the output stream");
        ZstdCodec.Decompress(output.ToArray(), 1 << 20).Should().Equal(raw);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Decompress_StreamedFrameWhoseBoundExceedsLimit_ThrowsInvalidData()
    {
        var raw = Sample(8_192);
        using var output = new MemoryStream();
        using (var stream = ZstdCodec.CreateCompressionStream(output, CompressionLevel.Optimal))
        {
            stream.Write(raw);
        }

        var framed = output.ToArray();
        ZstdCodec.GetDecompressedBound(framed).Should().BeGreaterThan((ulong)raw.Length,
            "a streamed frame declares no size, so the bound comes from its block count");

        var act = () => ZstdCodec.Decompress(framed, raw.Length);

        act.Should().Throw<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Decompress_CorruptPayload_ThrowsInvalidData()
    {
        var framed = ZstdCodec.Compress(Sample(4_096), CompressionLevel.Optimal);
        framed[0] ^= 0xFF; // break the frame magic number

        var act = () => ZstdCodec.Decompress(framed, 4_096);

        act.Should().Throw<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(CompressionLevel.Fastest, 1)]
    [InlineData(CompressionLevel.Optimal, ZstdCodec.OptimalLevel)]
    [InlineData(CompressionLevel.NoCompression, ZstdCodec.OptimalLevel)]
    [InlineData(CompressionLevel.SmallestSize, ZstdCodec.SmallestSizeLevel)]
    public void ToZstdLevel_MapsFrameworkLevels(CompressionLevel level, int expected)
        => ZstdCodec.ToZstdLevel(level).Should().Be(expected);
}

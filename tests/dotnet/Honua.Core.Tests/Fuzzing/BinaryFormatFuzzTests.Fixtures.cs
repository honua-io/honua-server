// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;
using Honua.Core.Configuration;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Core.Tests.Fuzzing;

public sealed partial class BinaryFormatFuzzTests
{
    private static WkbGeometryValidator CreateWkbValidator()
    {
        var topology = Substitute.For<IGeometryTopologyValidator>();
        return new WkbGeometryValidator(
            Options.Create(new LimitsOptions()),
            topology,
            NullLogger.Instance);
    }

    private static ICloudRangeReader CreateRangeReader(byte[] data)
    {
        var reader = Substitute.For<ICloudRangeReader>();
        reader.Provider.Returns(CloudStorageProvider.AwsS3);
        reader.GetObjectSizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((long)data.Length));
        reader.ReadRangeAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(ReadSlice(data, call.ArgAt<long>(2), call.ArgAt<int>(3))));
        return reader;
    }

    private static byte[] ReadSlice(byte[] data, long offset, int length)
    {
        if (offset < 0 || length <= 0 || offset >= data.Length)
        {
            return [];
        }

        var start = (int)offset;
        var count = Math.Min(length, data.Length - start);
        var slice = new byte[count];
        Buffer.BlockCopy(data, start, slice, 0, count);
        return slice;
    }

    private static byte[] MutatePayload(Random rng, byte[] baseline, int maxRandomLength)
    {
        var mode = rng.Next(4);
        if (mode == 0)
        {
            var pure = new byte[rng.Next(0, maxRandomLength)];
            rng.NextBytes(pure);
            return pure;
        }

        if (mode == 1 && baseline.Length > 0)
        {
            return baseline[..rng.Next(0, baseline.Length)];
        }

        var copy = (byte[])baseline.Clone();
        var flips = rng.Next(1, Math.Min(8, copy.Length + 1));
        for (var i = 0; i < flips && copy.Length > 0; i++)
        {
            copy[rng.Next(copy.Length)] ^= (byte)(1 << rng.Next(8));
        }

        if (mode == 3)
        {
            var suffix = new byte[rng.Next(1, 17)];
            rng.NextBytes(suffix);
            return [.. copy, .. suffix];
        }

        return copy;
    }

    private static byte[] CompressGzip(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(input);
        }

        return output.ToArray();
    }

    private static byte[] CompressZlib(byte[] input)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(input);
        }

        return output.ToArray();
    }

    private static byte[] BuildClassicTiff(uint nextIfdOffset)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write((uint)8);
        writer.Write((ushort)7);
        WriteClassicEntry(writer, new TiffTagSpec(256, 4, 256));
        WriteClassicEntry(writer, new TiffTagSpec(257, 4, 256));
        WriteClassicEntry(writer, new TiffTagSpec(259, 3, 1));
        WriteClassicEntry(writer, new TiffTagSpec(322, 4, 256));
        WriteClassicEntry(writer, new TiffTagSpec(323, 4, 256));
        WriteClassicEntry(writer, new TiffTagSpec(324, 4, 128));
        WriteClassicEntry(writer, new TiffTagSpec(325, 4, 64));
        writer.Write(nextIfdOffset);
        return stream.ToArray();
    }

    private static void WriteClassicEntry(BinaryWriter writer, TiffTagSpec entry)
    {
        writer.Write(entry.Tag);
        writer.Write(entry.Type);
        writer.Write((uint)1);
        writer.Write((uint)entry.Value);
    }

    private static byte[] BuildBigTiffHeaderAndIfd()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)43);
        writer.Write((ushort)8);
        writer.Write((ushort)0);
        writer.Write((long)16);
        writer.Write((long)2);
        WriteBigEntry(writer, new TiffTagSpec(256, 16, 128));
        WriteBigEntry(writer, new TiffTagSpec(257, 16, 128));
        writer.Write((long)0);
        return stream.ToArray();
    }

    private static void WriteBigEntry(BinaryWriter writer, TiffTagSpec entry)
    {
        writer.Write(entry.Tag);
        writer.Write(entry.Type);
        writer.Write((long)1);
        writer.Write(entry.Value);
    }

    private static byte[] BuildWkbPoint(double x, double y)
    {
        var buffer = new byte[21];
        buffer[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(5), x);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(13), y);
        return buffer;
    }

    private static byte[] BuildNestedGeometryCollection(int depth)
    {
        var buffer = new byte[(depth * 9) + 21];
        var offset = 0;
        for (var i = 0; i < depth; i++)
        {
            buffer[offset] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 1), 7);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 5), 1);
            offset += 9;
        }

        BuildWkbPoint(1.0, 2.0).CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    private readonly record struct TiffTagSpec(ushort Tag, ushort Type, long Value);
}

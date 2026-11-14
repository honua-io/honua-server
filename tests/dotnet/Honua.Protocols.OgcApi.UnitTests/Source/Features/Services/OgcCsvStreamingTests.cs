// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features.Services;

public sealed class OgcCsvStreamingTests
{
    [UnitTest]
    public async Task StreamCsv_WithEscapedFields_PreservesBufferedRepresentation()
    {
        GeoJsonFeature[] features =
        [
            Feature("=SUM(A1:A2)"), Feature("comma,quote\"and\nnewline"), Feature("-42.5"),
            new() { Id = "quoted,id", Geometry = new() { Type = "Point", CoordinatesJson = "[1,2]" } }
        ];
        using var stream = new MemoryStream();
        await OgcResponseFormatter.StreamCsvResponseAsync(stream, Enumerate(features), new(["name"]));
        Assert.Equal(OgcResponseFormatter.BuildCsvResponse(features, ["name"]),
            Encoding.UTF8.GetString(stream.ToArray()));
    }

    [UnitTest]
    public async Task StreamCsv_100kRows_FlushesBeforeReadingNextBatch()
    {
        using var sink = new CountingStream();
        await OgcResponseFormatter.StreamCsvResponseAsync(sink, Rows(), new(["name"]));
        Assert.True(sink.BytesWritten > 100_000_000);
        Assert.True(sink.LargestWrite < 128 * 1024);

        async IAsyncEnumerable<GeoJsonFeature> Rows()
        {
            for (var i = 0; i < 100_000; i++)
            {
                // Each row is larger than 1KB. An unflushed batch or eager collection
                // exceeds this fixed lag regardless of the total request size.
                Assert.True(sink.BytesWritten >= (long)Math.Max(0, i - 64) * 1024);
                yield return Feature(new string('x', 1024));
                if (i % 1024 == 0)
                {
                    await Task.Yield();
                }
            }
        }
    }

    [UnitTest]
    public async Task StreamCsv_FirstReadFails_DoesNotWritePartialHeader()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OgcResponseFormatter.StreamCsvResponseAsync(stream, FailedRows(), new(["name"])));
        Assert.Equal(0, stream.Length);

        static async IAsyncEnumerable<GeoJsonFeature> FailedRows()
        {
            await Task.FromException(new InvalidOperationException("reader unavailable"));
            yield break;
        }
    }

    private static GeoJsonFeature Feature(string name) => new()
    {
        Id = 1,
        Properties = new Dictionary<string, object?> { ["name"] = name }
    };

    private static async IAsyncEnumerable<GeoJsonFeature> Enumerate(
        IEnumerable<GeoJsonFeature> features,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return feature;
        }

        await Task.CompletedTask;
    }

    private sealed class CountingStream : Stream
    {
        public long BytesWritten { get; private set; }
        public int LargestWrite { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int count)
        {
            BytesWritten += count;
            LargestWrite = Math.Max(LargestWrite, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write([], 0, buffer.Length);
            return ValueTask.CompletedTask;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

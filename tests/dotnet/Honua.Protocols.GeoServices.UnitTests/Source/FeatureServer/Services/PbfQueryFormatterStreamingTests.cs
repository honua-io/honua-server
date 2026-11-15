// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed partial class PbfQueryFormatterTests
{
    [Fact]
    public async Task FormatAsPbfStream_LargeAttribute_WritesIdenticalBytesInBoundedChunks()
    {
        var layer = CreatePointLayer();
        var feature = Feature.Create(42, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", new string('x', 200_000)));
        var query = QueryResult<Feature>.Create(1, [feature]);
        var (expected, _) = _sut.FormatAsPbf(query, layer, false, null, false, false, null, null, null);
        var (response, contentType) = _sut.FormatAsPbfStream(query, layer, new PbfStreamEncodingOptions
        {
            ReturnGeometry = false,
            OutputSrid = null,
            ReturnZ = false,
            ReturnM = false,
            GeometryPrecision = null,
            MaxAllowableOffset = null,
            OutFields = null
        });
        using var body = new BinaryWriteDigest();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        await response.ExecuteAsync(context);
        contentType.Should().Be("application/x-protobuf");
        body.LargestWrite.Should().BeLessThanOrEqualTo(64 * 1024);
        body.Length.Should().Be(expected.Length);
        body.Digest().Should().Equal(SHA256.HashData(expected));
    }

    [Fact]
    public async Task FormatAsPbfStream_HundredThousandFeatures_DoesNotRetainAnEncodedPage()
    {
        const int count = 100_000;
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("name", new string('x', 1024));
        var features = Enumerable.Range(1, count).Select(id => Feature.Create(id, null, attributes)).ToImmutableArray();
        var query = QueryResult<Feature>.Create(count, features);
        var baseline = GC.GetTotalMemory(true);
        var (response, _) = _sut.FormatAsPbfStream(query, CreatePointLayer(), new PbfStreamEncodingOptions
        {
            ReturnGeometry = false,
            OutputSrid = null,
            ReturnZ = false,
            ReturnM = false,
            GeometryPrecision = null,
            MaxAllowableOffset = null,
            OutFields = null
        });
        (GC.GetTotalMemory(true) - baseline).Should().BeLessThan(8 * 1024 * 1024);
        using var body = new BinaryWriteDigest();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        await response.ExecuteAsync(context);
        body.Length.Should().BeGreaterThan(100_000_000);
        body.LargestWrite.Should().BeLessThanOrEqualTo(64 * 1024);
        GC.KeepAlive(query);
    }

    private sealed class BinaryWriteDigest : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;
        public int LargestWrite { get; private set; }
        public override long Length => _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public byte[] Digest() => _hash.GetHashAndReset();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            LargestWrite = Math.Max(LargestWrite, buffer.Length);
            _length += buffer.Length;
            _hash.AppendData(buffer);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}

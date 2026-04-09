// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Compression;

namespace Honua.Server.Tests.Features.Infrastructure.Compression;

public sealed class AdaptiveCompressionProviderTests
{
    [Fact]
    public async Task AdaptiveGzipStream_FlushAsync_WithBufferedContent_DoesNotUseSynchronousIo()
    {
        var output = new AsyncOnlyBufferingStream();
        var stream = new AdaptiveGzipStream(
            output,
            new AdaptiveCompressionOptions
            {
                FastCompressionThreshold = int.MaxValue
            });

        await stream.WriteAsync(Encoding.UTF8.GetBytes("buffered response payload"));
        await stream.FlushAsync();
        await stream.DisposeAsync();

        output.BytesWritten.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AdaptiveBrotliStream_WriteAsync_CrossingThreshold_DoesNotUseSynchronousIo()
    {
        var output = new AsyncOnlyBufferingStream();
        var stream = new AdaptiveBrotliStream(
            output,
            new AdaptiveCompressionOptions
            {
                FastCompressionThreshold = 1
            });

        await stream.WriteAsync(Encoding.UTF8.GetBytes("payload"));
        await stream.FlushAsync();
        await stream.DisposeAsync();

        output.BytesWritten.Should().BeGreaterThan(0);
    }

    private sealed class AsyncOnlyBufferingStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public long BytesWritten => _inner.Length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
            => throw new InvalidOperationException("Synchronous flush is not allowed.");

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new InvalidOperationException("Synchronous write is not allowed.");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}

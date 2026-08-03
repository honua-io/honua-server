// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Raster;

namespace Honua.Postgres.Tests.Features.Raster;

public sealed class NpgsqlKnownLengthReadStreamTests
{
    [Fact]
    public async Task CopyToAsync_StreamsNonSeekableInputUsingDeclaredLength()
    {
        var source = new NonSeekableReadStream([1, 2, 3, 4]);
        await using var stream = new NpgsqlKnownLengthReadStream(source, 4, leaveOpen: true);
        await using var destination = new MemoryStream();

        Assert.True(stream.CanSeek);
        Assert.Equal(4, stream.Length);
        Assert.Equal(0, stream.Position);
        Assert.Equal(0, source.BytesRead);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Current));

        await stream.CopyToAsync(destination);

        Assert.Equal([1, 2, 3, 4], destination.ToArray());
        Assert.Equal(4, stream.Position);
        Assert.Equal(4, source.BytesRead);
    }

    [Fact]
    public async Task CopyToAsync_RejectsResponseShorterThanDeclaredLength()
    {
        var source = new NonSeekableReadStream([1, 2]);
        await using var stream = new NpgsqlKnownLengthReadStream(source, 3);

        var exception = await Assert.ThrowsAsync<EndOfStreamException>(
            () => stream.CopyToAsync(Stream.Null));

        Assert.Contains("2 bytes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("3 bytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyToAsync_RejectsResponseLongerThanDeclaredLength()
    {
        var source = new NonSeekableReadStream([1, 2, 3]);
        await using var stream = new NpgsqlKnownLengthReadStream(source, 2);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => stream.CopyToAsync(Stream.Null));

        Assert.Contains("exceeded", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, source.BytesRead);
    }

    [Fact]
    public async Task DisposeAsync_DisposesProviderStreamByDefault()
    {
        var source = new NonSeekableReadStream([1]);
        var stream = new NpgsqlKnownLengthReadStream(source, 1);

        await stream.DisposeAsync();

        Assert.True(source.IsDisposed);
    }

    private sealed class NonSeekableReadStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public long BytesRead { get; private set; }

        public bool IsDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            await _inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}

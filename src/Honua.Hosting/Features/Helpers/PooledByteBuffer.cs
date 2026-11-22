// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;

namespace Honua.Infrastructure.Helpers;

// A write-only buffer rented from ArrayPool<byte>.Shared, usable as a Stream (for XmlWriter) and as an
// IBufferWriter<byte> (for Utf8JsonWriter). Growing copies into a larger rented array and returns the old
// one; disposing returns the current array.
internal sealed class PooledByteBuffer : Stream, IBufferWriter<byte>
{
    internal const int DefaultInitialCapacity = 16 * 1024;

    private byte[] _buffer;
    private int _written;
    private bool _disposed;

    internal PooledByteBuffer(int initialCapacity = DefaultInitialCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed;

    public override long Length => _written;

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _written);
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureCapacity(buffer.Length);
        buffer.CopyTo(_buffer.AsSpan(_written));
        _written += buffer.Length;
    }

    public override void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_written++] = value;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            var rented = _buffer;
            _buffer = [];
            _written = 0;
            ArrayPool<byte>.Shared.Return(rented);
        }

        base.Dispose(disposing);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var required = Math.Max(sizeHint, 1);
        if (_buffer.Length - _written >= required)
        {
            return;
        }

        var minimum = (long)_written + required;
        if (minimum > Array.MaxLength)
        {
            throw new InvalidOperationException("The pooled buffer cannot grow past the maximum array length.");
        }

        var next = ArrayPool<byte>.Shared.Rent((int)Math.Min(Array.MaxLength, Math.Max(minimum, (long)_buffer.Length * 2)));
        _buffer.AsSpan(0, _written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }
}

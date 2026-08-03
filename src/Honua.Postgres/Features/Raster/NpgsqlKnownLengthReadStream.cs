// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// Presents a forward-only object-store response to Npgsql with a trusted, pre-validated length.
/// Npgsql otherwise copies non-seekable bytea streams into a <see cref="MemoryStream"/> during
/// parameter binding. Reporting the descriptor length keeps PostGIS imports streaming, while the
/// read guards fail the command if the provider response does not match that identity.
/// </summary>
internal sealed class NpgsqlKnownLengthReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private long _position;
    private bool _endVerified;
    private bool _disposed;

    public NpgsqlKnownLengthReadStream(Stream inner, long length, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead)
        {
            throw new ArgumentException("The underlying stream must be readable.", nameof(inner));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(length);

        // PostgreSQL's bind protocol and Npgsql's bytea converter use a signed 32-bit value.
        if (length > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "PostGIS raster imports cannot exceed the PostgreSQL bytea parameter limit.");
        }

        _inner = inner;
        _length = length;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => !_disposed && _inner.CanRead;

    // Npgsql uses CanSeek only to decide whether Length/Position can supply the bind size. The
    // stream remains forward-only and every actual Seek operation is rejected.
    public override bool CanSeek => !_disposed;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set => throw new NotSupportedException("The known-length parameter stream is forward-only.");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_position == _length)
        {
            VerifyEnd();
            return 0;
        }

        var count = (int)Math.Min(buffer.Length, _length - _position);
        var read = _inner.Read(buffer[..count]);
        Advance(read);
        if (_position == _length)
        {
            VerifyEnd();
        }

        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_position == _length)
        {
            await VerifyEndAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var count = (int)Math.Min(buffer.Length, _length - _position);
        var read = await _inner.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        Advance(read);
        if (_position == _length)
        {
            await VerifyEndAsync(cancellationToken).ConfigureAwait(false);
        }

        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        throw new NotSupportedException("The known-length parameter stream is forward-only.");
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException("The known-length parameter stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The known-length parameter stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing && !_leaveOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void Advance(int read)
    {
        if (read <= 0)
        {
            throw new EndOfStreamException(
                $"Raster object ended after {_position} bytes; {_length} bytes were declared.");
        }

        _position += read;
    }

    private void VerifyEnd()
    {
        if (_endVerified)
        {
            return;
        }

        if (_inner.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"Raster object exceeded its declared length of {_length} bytes.");
        }

        _endVerified = true;
    }

    private async ValueTask VerifyEndAsync(CancellationToken cancellationToken)
    {
        if (_endVerified)
        {
            return;
        }

        var probe = new byte[1];
        if (await _inner.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException(
                $"Raster object exceeded its declared length of {_length} bytes.");
        }

        _endVerified = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.FileStorage;

// Gives a forward-only download a length and forward seeking, which is what a ranged HTTP response
// needs: it seeks once to the range start and reads on. Seeking forward reads and discards.
internal sealed class ForwardSeekReadStream(Stream source, long length) : Stream
{
    private const int DiscardBufferBytes = 81920;

    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < _position || target > length)
        {
            throw new NotSupportedException("A staged output download can only seek forward within its length.");
        }

        var discard = new byte[(int)Math.Min(DiscardBufferBytes, Math.Max(1, target - _position))];
        while (_position < target)
        {
            var read = source.Read(discard, 0, (int)Math.Min(discard.Length, target - _position));
            if (read == 0)
            {
                throw new EndOfStreamException("The staged output ended before the requested position.");
            }

            _position += read;
        }

        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Close()
    {
        source.Dispose();
        base.Close();
    }

    public override async ValueTask DisposeAsync()
    {
        await source.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

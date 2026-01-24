// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.IO;

/// <summary>
/// Stream wrapper that forwards operations to an inner stream.
/// </summary>
public abstract class DelegatingStream : Stream
{
    protected DelegatingStream(Stream inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>
    /// The wrapped stream.
    /// </summary>
    protected Stream Inner { get; }

    public override bool CanRead => Inner.CanRead;

    public override bool CanSeek => Inner.CanSeek;

    public override bool CanWrite => Inner.CanWrite;

    public override long Length => Inner.Length;

    public override long Position
    {
        get => Inner.Position;
        set => Inner.Position = value;
    }

    public override void Flush() => Inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => Inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);

    public override void SetLength(long value) => Inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => Inner.Write(buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        Inner.WriteAsync(buffer, cancellationToken);
}

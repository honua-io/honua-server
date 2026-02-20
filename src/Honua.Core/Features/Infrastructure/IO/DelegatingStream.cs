// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.IO;

/// <summary>
/// Stream wrapper that forwards operations to an inner stream.
/// </summary>
public abstract class DelegatingStream : Stream
{
    /// <inheritdoc/>
    protected DelegatingStream(Stream inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>
    /// The wrapped stream.
    /// </summary>
    protected Stream Inner { get; }

    /// <inheritdoc/>
    public override bool CanRead => Inner.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => Inner.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => Inner.CanWrite;

    /// <inheritdoc/>
    public override long Length => Inner.Length;

    /// <inheritdoc/>
    public override long Position
    {
        get => Inner.Position;
        set => Inner.Position = value;
    }

    /// <inheritdoc/>
    public override void Flush() => Inner.Flush();

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer) => Inner.Read(buffer);

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Inner.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Inner.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);

    /// <inheritdoc/>
    public override void SetLength(long value) => Inner.SetLength(value);

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer) => Inner.Write(buffer);

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Inner.WriteAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        Inner.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await Inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

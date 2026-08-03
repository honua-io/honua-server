// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure.Storage.Blobs.Models;

namespace Honua.FileStorage;

/// <summary>
/// Wrapper stream that ensures the owning <see cref="BlobDownloadStreamingResult"/> is disposed
/// when the stream is disposed. Disposing the content stream alone does NOT release the
/// underlying HTTP response/network resources held by the result, so both must be disposed.
/// </summary>
internal sealed class BlobDownloadStream : Stream
{
    private readonly BlobDownloadStreamingResult _result;
    private readonly Stream _inner;
    private bool _disposed;

    public BlobDownloadStream(BlobDownloadStreamingResult result)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
        _inner = result.Content;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            // The SDK result owns and disposes its content stream.
            _result.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

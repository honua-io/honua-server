// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using Honua.Core.Features.Infrastructure.IO;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Compression;

internal sealed class AdaptiveBrotliCompressionProvider : ICompressionProvider
{
    private readonly AdaptiveCompressionOptions _options;

    public AdaptiveBrotliCompressionProvider(IOptions<AdaptiveCompressionOptions> options)
    {
        _options = options.Value;
    }

    public string EncodingName => "br";

    public bool SupportsFlush => true;

    public Stream CreateStream(Stream outputStream)
    {
        return new AdaptiveBrotliStream(outputStream, _options);
    }
}

internal sealed class AdaptiveGzipCompressionProvider : ICompressionProvider
{
    private readonly AdaptiveCompressionOptions _options;

    public AdaptiveGzipCompressionProvider(IOptions<AdaptiveCompressionOptions> options)
    {
        _options = options.Value;
    }

    public string EncodingName => "gzip";

    public bool SupportsFlush => true;

    public Stream CreateStream(Stream outputStream)
    {
        return new AdaptiveGzipStream(outputStream, _options);
    }
}

internal sealed class AdaptiveZstdCompressionProvider : ICompressionProvider
{
    private readonly AdaptiveCompressionOptions _options;

    public AdaptiveZstdCompressionProvider(IOptions<AdaptiveCompressionOptions> options)
    {
        _options = options.Value;
    }

    public string EncodingName => "zstd";

    public bool SupportsFlush => true;

    public Stream CreateStream(Stream outputStream)
    {
        return new AdaptiveZstdStream(outputStream, _options);
    }
}

public sealed class AdaptiveCompressionOptions
{
    public int FastCompressionThreshold { get; set; } = 4096; // 4KB - use Fastest for small responses
    public int OptimalCompressionThreshold { get; set; } = 64 * 1024; // 64KB - use Optimal for large responses
    public CompressionLevel DefaultLevel { get; set; } = CompressionLevel.Fastest;
    public CompressionLevel SmallContentLevel { get; set; } = CompressionLevel.Fastest;
    public CompressionLevel MediumContentLevel { get; set; } = CompressionLevel.SmallestSize;
    public CompressionLevel LargeContentLevel { get; set; } = CompressionLevel.Optimal;
}

internal static class AdaptiveCompressionLevelSelector
{
    internal static CompressionLevel Select(long contentSize, AdaptiveCompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var fastThreshold = Math.Max(1, options.FastCompressionThreshold);
        var optimalThreshold = Math.Max(fastThreshold + 1, options.OptimalCompressionThreshold);

        return contentSize < fastThreshold
            ? options.SmallContentLevel
            : contentSize < optimalThreshold
                ? options.MediumContentLevel
                : options.LargeContentLevel;
    }
}

internal abstract class AdaptiveCompressionStream : Stream
{
    private readonly Stream _outputStream;
    private readonly AdaptiveCompressionOptions _options;
    private readonly MemoryStream _buffer;
    private Stream? _compressionStream;
    private bool _disposed;

    protected AdaptiveCompressionStream(Stream outputStream, AdaptiveCompressionOptions options)
    {
        _outputStream = outputStream;
        _options = options;
        _buffer = new MemoryStream();
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_compressionStream == null)
        {
            // Buffer initial writes to determine content size
            _buffer.Write(buffer, offset, count);

            // Once we have enough data or reach a threshold, choose compression level
            if (_buffer.Length >= _options.FastCompressionThreshold)
            {
                InitializeCompressionStream();
            }
        }
        else
        {
            _compressionStream.Write(buffer, offset, count);
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_compressionStream == null)
        {
            await _buffer.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);

            if (_buffer.Length >= _options.FastCompressionThreshold)
            {
                await InitializeCompressionStreamAsync(cancellationToken);
            }
        }
        else
        {
            await _compressionStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_compressionStream == null)
        {
            await _buffer.WriteAsync(buffer, cancellationToken);

            if (_buffer.Length >= _options.FastCompressionThreshold)
            {
                await InitializeCompressionStreamAsync(cancellationToken);
            }
        }
        else
        {
            await _compressionStream.WriteAsync(buffer, cancellationToken);
        }
    }

    private void InitializeCompressionStream()
    {
        var level = DetermineCompressionLevel(_buffer.Length);
        _compressionStream = CreateCompressionStream(_outputStream, level);

        // Write buffered data to compression stream directly from the MemoryStream's
        // internal buffer instead of copying it out via ToArray().
        if (_buffer.Length > 0)
        {
            _compressionStream.Write(_buffer.GetBuffer(), 0, (int)_buffer.Length);
            _buffer.SetLength(0);
        }
    }

    private async Task InitializeCompressionStreamAsync(CancellationToken cancellationToken)
    {
        if (_compressionStream != null)
        {
            return;
        }

        var level = DetermineCompressionLevel(_buffer.Length);
        _compressionStream = CreateCompressionStream(_outputStream, level);

        if (_buffer.Length > 0)
        {
            // Write directly from the MemoryStream's internal buffer instead of copying
            // it out via ToArray().
            await _compressionStream.WriteAsync(
                _buffer.GetBuffer().AsMemory(0, (int)_buffer.Length), cancellationToken);
            _buffer.SetLength(0);
        }
    }

    // Called once the buffered prefix has chosen a level; the codec stream must leave destination open.
    protected abstract Stream CreateCompressionStream(Stream destination, CompressionLevel level);

    private CompressionLevel DetermineCompressionLevel(long contentSize)
        => AdaptiveCompressionLevelSelector.Select(contentSize, _options);

    public override void Flush()
    {
        if (_compressionStream == null && _buffer.Length > 0)
        {
            InitializeCompressionStream();
        }
        _compressionStream?.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_compressionStream == null && _buffer.Length > 0)
        {
            await InitializeCompressionStreamAsync(cancellationToken);
        }
        if (_compressionStream != null)
        {
            await _compressionStream.FlushAsync(cancellationToken);
        }
    }

    protected override void Dispose(bool disposing)
    {
        // DisposeAsync releases the buffer before Stream.DisposeAsync calls back here.
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (_compressionStream == null && _buffer.Length > 0)
            {
                InitializeCompressionStream();
            }
            _compressionStream?.Dispose();
            _buffer.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_compressionStream == null && _buffer.Length > 0)
        {
            await InitializeCompressionStreamAsync(CancellationToken.None);
        }

        if (_compressionStream != null)
        {
            await _compressionStream.DisposeAsync();
        }

        await _buffer.DisposeAsync();
        _disposed = true;
        await base.DisposeAsync();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed class AdaptiveBrotliStream(Stream outputStream, AdaptiveCompressionOptions options)
    : AdaptiveCompressionStream(outputStream, options)
{
    protected override Stream CreateCompressionStream(Stream destination, CompressionLevel level)
        => new BrotliStream(destination, level, leaveOpen: true);
}

internal sealed class AdaptiveGzipStream(Stream outputStream, AdaptiveCompressionOptions options)
    : AdaptiveCompressionStream(outputStream, options)
{
    protected override Stream CreateCompressionStream(Stream destination, CompressionLevel level)
        => new GZipStream(destination, level, leaveOpen: true);
}

internal sealed class AdaptiveZstdStream(Stream outputStream, AdaptiveCompressionOptions options)
    : AdaptiveCompressionStream(outputStream, options)
{
    protected override Stream CreateCompressionStream(Stream destination, CompressionLevel level)
        => ZstdCodec.CreateCompressionStream(destination, level);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Compression;

/// <summary>
/// PERFORMANCE FIX: Adaptive compression provider that adjusts compression level based on content size
/// Small responses use Fastest for low latency, large responses use Optimal for better compression ratios
/// </summary>
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

/// <summary>
/// PERFORMANCE FIX: Adaptive Gzip compression provider for fallback scenarios
/// </summary>
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

/// <summary>
/// PERFORMANCE FIX: Configuration options for adaptive compression
/// </summary>
public sealed class AdaptiveCompressionOptions
{
    public int FastCompressionThreshold { get; set; } = 4096; // 4KB - use Fastest for small responses
    public int OptimalCompressionThreshold { get; set; } = 64 * 1024; // 64KB - use Optimal for large responses
    public CompressionLevel DefaultLevel { get; set; } = CompressionLevel.Fastest;
    public CompressionLevel SmallContentLevel { get; set; } = CompressionLevel.Fastest;
    public CompressionLevel MediumContentLevel { get; set; } = CompressionLevel.SmallestSize;
    public CompressionLevel LargeContentLevel { get; set; } = CompressionLevel.Optimal;
}

/// <summary>
/// PERFORMANCE FIX: Adaptive Brotli stream that chooses compression level based on content size
/// </summary>
internal sealed class AdaptiveBrotliStream : Stream
{
    private readonly Stream _outputStream;
    private readonly AdaptiveCompressionOptions _options;
    private readonly MemoryStream _buffer;
    private BrotliStream? _compressionStream;

    public AdaptiveBrotliStream(Stream outputStream, AdaptiveCompressionOptions options)
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
            if (_buffer.Length >= _options.FastCompressionThreshold || ShouldFlush())
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
            await _buffer.WriteAsync(buffer, offset, count, cancellationToken);

            if (_buffer.Length >= _options.FastCompressionThreshold || ShouldFlush())
            {
                InitializeCompressionStream();
            }
        }
        else
        {
            await _compressionStream.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }

    private void InitializeCompressionStream()
    {
        var level = DetermineCompressionLevel(_buffer.Length);
        _compressionStream = new BrotliStream(_outputStream, level, leaveOpen: true);

        // Write buffered data to compression stream
        if (_buffer.Length > 0)
        {
            var bufferedData = _buffer.ToArray();
            _compressionStream.Write(bufferedData, 0, bufferedData.Length);
        }
    }

    private CompressionLevel DetermineCompressionLevel(long contentSize)
    {
        return contentSize switch
        {
            < 4096 => _options.SmallContentLevel,  // Small content: prioritize speed
            < 65536 => _options.MediumContentLevel, // Medium content: balanced
            _ => _options.LargeContentLevel        // Large content: prioritize compression ratio
        };
    }

    private bool ShouldFlush()
    {
        // Force initialization if we're about to flush/close without reaching threshold
        return false; // Simple implementation for now
    }

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
            InitializeCompressionStream();
        }
        if (_compressionStream != null)
        {
            await _compressionStream.FlushAsync(cancellationToken);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_compressionStream == null && _buffer.Length > 0)
            {
                InitializeCompressionStream();
            }
            _compressionStream?.Dispose();
            _buffer.Dispose();
        }
        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// PERFORMANCE FIX: Adaptive Gzip stream implementation
/// </summary>
internal sealed class AdaptiveGzipStream : Stream
{
    private readonly Stream _outputStream;
    private readonly AdaptiveCompressionOptions _options;
    private readonly MemoryStream _buffer;
    private GZipStream? _compressionStream;

    public AdaptiveGzipStream(Stream outputStream, AdaptiveCompressionOptions options)
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
            _buffer.Write(buffer, offset, count);

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

    private void InitializeCompressionStream()
    {
        var level = DetermineCompressionLevel(_buffer.Length);
        _compressionStream = new GZipStream(_outputStream, level, leaveOpen: true);

        if (_buffer.Length > 0)
        {
            var bufferedData = _buffer.ToArray();
            _compressionStream.Write(bufferedData, 0, bufferedData.Length);
        }
    }

    private CompressionLevel DetermineCompressionLevel(long contentSize)
    {
        return contentSize switch
        {
            < 4096 => _options.SmallContentLevel,
            < 65536 => _options.MediumContentLevel,
            _ => _options.LargeContentLevel
        };
    }

    public override void Flush()
    {
        if (_compressionStream == null && _buffer.Length > 0)
        {
            InitializeCompressionStream();
        }
        _compressionStream?.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_compressionStream == null && _buffer.Length > 0)
            {
                InitializeCompressionStream();
            }
            _compressionStream?.Dispose();
            _buffer.Dispose();
        }
        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

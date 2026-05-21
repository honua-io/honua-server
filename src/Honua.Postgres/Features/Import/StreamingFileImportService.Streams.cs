// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using System.Runtime.CompilerServices;
using Honua.Core.Features.Import.Services;
using NetTopologySuite.Features;

namespace Honua.Postgres.Features.Import;

internal sealed partial class StreamingFileImportService
{
    private sealed record ShapefileScratch(string DirectoryPath, string ShpPath);

    private sealed record GeoPackageScratch(string DirectoryPath, string FilePath);

    private sealed record KmzScratch(string DirectoryPath, string KmlPath);

    private sealed record FileGdbScratch(string DirectoryPath, string GdbPath);

    private sealed record GeoParquetScratch(Stream Stream, string? ScratchDir) : IDisposable
    {
        public void Dispose()
        {
            // Only dispose the stream when the scratch owns it (non-seekable buffered copy).
            // When ScratchDir is null, the scratch wraps the caller's original seekable stream
            // and the caller is responsible for its lifetime.
            if (ScratchDir != null)
            {
                Stream.Dispose();
            }
        }
    }

    private sealed record GeoPackageLayerInfo(string TableName, string GeometryColumn, int? Srid);

    private static async IAsyncEnumerable<IFeature> EmptyFeatureStream(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// Copies a non-seekable stream into a seekable temporary file (DeleteOnClose).
    /// Delegates to <see cref="ImportStreamHelper.SpillToSeekableTempAsync"/>.
    /// </summary>
    private static Task<FileStream> SpillToSeekableTempAsync(Stream source, CancellationToken cancellationToken)
        => ImportStreamHelper.SpillToSeekableTempAsync(source, cancellationToken);

    private sealed class PrefixedReadStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _prefix;
        private readonly Stream _inner;
        private int _prefixOffset;
        // Track logical position for diagnostics even on non-seekable wrappers.
        private long _position;

        public PrefixedReadStream(ReadOnlyMemory<byte> prefix, Stream inner)
        {
            _prefix = prefix;
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0)
            {
                return 0;
            }

            var bytesRead = ReadPrefix(buffer.AsSpan(offset, count));
            if (bytesRead < count)
            {
                bytesRead += _inner.Read(buffer, offset + bytesRead, count - bytesRead);
            }

            _position += bytesRead;
            return bytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            var bytesRead = ReadPrefix(buffer);
            if (bytesRead < buffer.Length)
            {
                bytesRead += _inner.Read(buffer[bytesRead..]);
            }

            _position += bytesRead;
            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var bytesRead = ReadPrefix(buffer.AsSpan(offset, count));
            if (bytesRead < count)
            {
                bytesRead += await _inner.ReadAsync(buffer.AsMemory(offset + bytesRead, count - bytesRead), cancellationToken);
            }

            _position += bytesRead;
            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytesRead = ReadPrefix(buffer.Span);
            if (bytesRead < buffer.Length)
            {
                bytesRead += await _inner.ReadAsync(buffer[bytesRead..], cancellationToken);
            }

            _position += bytesRead;
            return bytesRead;
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private int ReadPrefix(Span<byte> buffer)
        {
            var remaining = _prefix.Length - _prefixOffset;
            if (remaining <= 0)
            {
                return 0;
            }

            var toCopy = Math.Min(buffer.Length, remaining);
            _prefix.Span.Slice(_prefixOffset, toCopy).CopyTo(buffer[..toCopy]);
            _prefixOffset += toCopy;
            return toCopy;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    private sealed record ShapefileEntries(
        string BaseName,
        ZipArchiveEntry ShpEntry,
        ZipArchiveEntry DbfEntry,
        ZipArchiveEntry? ShxEntry,
        ZipArchiveEntry? PrjEntry,
        ZipArchiveEntry? CpgEntry);

    private sealed class ShapefileEntryGroup
    {
        public ShapefileEntryGroup(string baseName)
        {
            BaseName = baseName;
        }

        public string BaseName { get; }
        public ZipArchiveEntry? Shp { get; set; }
        public ZipArchiveEntry? Dbf { get; set; }
        public ZipArchiveEntry? Shx { get; set; }
        public ZipArchiveEntry? Prj { get; set; }
        public ZipArchiveEntry? Cpg { get; set; }
    }

    private sealed class ArchiveExtractionBudget
    {
        public ArchiveExtractionBudget(long maxTotalBytes, long maxEntryBytes, double maxCompressionRatio)
        {
            MaxTotalBytes = maxTotalBytes;
            MaxEntryBytes = maxEntryBytes;
            MaxCompressionRatio = maxCompressionRatio;
        }

        public long MaxTotalBytes { get; }
        public long MaxEntryBytes { get; }
        public double MaxCompressionRatio { get; }
        public long TotalExtractedBytes { get; set; }
    }
}

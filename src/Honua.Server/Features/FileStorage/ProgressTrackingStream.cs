// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.FileStorage;

/// <summary>
/// Stream wrapper that tracks upload progress and reports it via IProgress callback.
/// </summary>
internal sealed class ProgressTrackingStream : Stream
{
    private readonly Stream _innerStream;
    private readonly IProgress<UploadProgress>? _progress;
    private readonly string _uploadId;
    private readonly string _fileName;
    private readonly long _totalLength;
    private readonly Stopwatch _stopwatch;

    private long _bytesProcessed;
    private DateTimeOffset _lastProgressUpdate;
    private bool _disposed;

    public ProgressTrackingStream(
        Stream innerStream,
        IProgress<UploadProgress>? progress,
        string uploadId,
        string fileName,
        long totalLength)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _progress = progress;
        _uploadId = uploadId;
        _fileName = fileName;
        _totalLength = totalLength;
        _stopwatch = Stopwatch.StartNew();
        _lastProgressUpdate = DateTimeOffset.UtcNow;

        // Report initial progress
        ReportProgress(OperationStatus.Processing, "Starting upload");
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _innerStream.Read(buffer, offset, count);
        UpdateProgress(bytesRead);
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        UpdateProgress(bytesRead);
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer, cancellationToken);
        UpdateProgress(bytesRead);
        return bytesRead;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _innerStream.Write(buffer, offset, count);
        UpdateProgress(count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _innerStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        UpdateProgress(count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _innerStream.WriteAsync(buffer, cancellationToken);
        UpdateProgress(buffer.Length);
    }

    public override void Flush() => _innerStream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _innerStream.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
    public override void SetLength(long value) => _innerStream.SetLength(value);

    private void UpdateProgress(int bytesProcessed)
    {
        if (_progress == null || bytesProcessed <= 0)
            return;

        _bytesProcessed += bytesProcessed;
        var now = DateTimeOffset.UtcNow;

        // Only report progress every 100ms to avoid excessive callbacks
        if (now - _lastProgressUpdate < TimeSpan.FromMilliseconds(100) && _bytesProcessed < _totalLength)
            return;

        ReportProgress(OperationStatus.Processing, "Uploading");
        _lastProgressUpdate = now;
    }

    private void ReportProgress(OperationStatus status, string phase)
    {
        if (_progress == null)
            return;

        try
        {
            var progressRecord = new UploadProgress
            {
                UploadId = _uploadId,
                Status = status,
                BytesUploaded = _bytesProcessed,
                TotalBytes = _totalLength,
                FileName = _fileName,
                StartedAt = DateTimeOffset.UtcNow - _stopwatch.Elapsed,
                CurrentPhase = phase,
                CompletedAt = status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled
                    ? DateTimeOffset.UtcNow
                    : null
            };

            _progress.Report(progressRecord);
        }
        catch (Exception)
        {
            // Ignore progress reporting errors
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            // Report final progress
            if (_progress != null)
            {
                try
                {
                    var status = _bytesProcessed >= _totalLength ? OperationStatus.Completed : OperationStatus.Failed;
                    var phase = _bytesProcessed >= _totalLength ? "Upload completed" : "Upload incomplete";
                    ReportProgress(status, phase);
                }
                catch (Exception)
                {
                    // Ignore final progress reporting errors
                }
            }

            _stopwatch.Stop();
            _innerStream.Dispose();
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            // Report final progress
            if (_progress != null)
            {
                try
                {
                    var status = _bytesProcessed >= _totalLength ? OperationStatus.Completed : OperationStatus.Failed;
                    var phase = _bytesProcessed >= _totalLength ? "Upload completed" : "Upload incomplete";
                    ReportProgress(status, phase);
                }
                catch (Exception)
                {
                    // Ignore final progress reporting errors
                }
            }

            _stopwatch.Stop();
            await _innerStream.DisposeAsync();
            _disposed = true;
        }

        await base.DisposeAsync();
    }
}

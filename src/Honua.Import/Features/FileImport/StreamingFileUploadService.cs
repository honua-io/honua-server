// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Import.FileImport;

/// <summary>
/// Exposes upload-queue backpressure metrics (<see cref="IUploadQueueMetricsProvider"/>)
/// for the production monitoring, metrics, and health-check surfaces.
/// </summary>
/// <remarks>
/// The former server-side upload-queue processing path was removed (#2459): staged import
/// input is handled inline by the import endpoints (within-request scratch) and durable
/// cross-request handoff goes through the shared object store, so there is no in-process
/// upload queue to drain. The queue depth/active counters therefore report an idle queue;
/// the metrics surface is retained because health checks and monitoring endpoints consume it.
/// </remarks>
internal sealed class StreamingFileUploadService : IDisposable, IUploadQueueMetricsProvider
{
    private readonly FileUploadOptions _options;
    private readonly SemaphoreSlim _processingSlot;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingFileUploadService"/> class.
    /// </summary>
    /// <param name="options">File upload options.</param>
    public StreamingFileUploadService(IOptions<FileUploadOptions> options)
    {
        _options = options.Value;

        // Limit concurrent upload processing
        _processingSlot = new SemaphoreSlim(_options.MaxConcurrentUploads, _options.MaxConcurrentUploads);
    }

    /// <inheritdoc />
    public UploadQueueSnapshot GetQueueSnapshot()
    {
        // No in-process upload queue remains (#2459): queue depth is always zero, and with no
        // slots ever taken the active-upload count derives from the semaphore's current count.
        return new UploadQueueSnapshot(
            0,
            _options.MaxQueuedUploads,
            _options.MaxConcurrentUploads - _processingSlot.CurrentCount,
            _options.MaxConcurrentUploads);
    }

    /// <summary>
    /// Disposes the service and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _processingSlot.Dispose();
    }
}

/// <summary>
/// File upload configuration options.
/// </summary>
internal sealed class FileUploadOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "FileUpload";

    /// <summary>
    /// Maximum file size in bytes. Default is 100MB.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    /// Maximum concurrent uploads. Default is 5.
    /// </summary>
    public int MaxConcurrentUploads { get; set; } = 5;

    /// <summary>
    /// Maximum queued uploads. Default is 20.
    /// </summary>
    public int MaxQueuedUploads { get; set; } = 20;
}

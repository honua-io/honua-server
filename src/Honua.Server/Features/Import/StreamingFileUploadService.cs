// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Threading.Channels;
using Honua.Server.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Import;

/// <summary>
/// Service for handling streaming file uploads with backpressure control.
/// Prevents memory exhaustion from large file uploads and provides upload queue management.
/// </summary>
internal sealed class StreamingFileUploadService : IDisposable, IUploadQueueMetricsProvider
{
    private readonly Channel<FileUploadJob> _uploadQueue;
    private readonly ChannelWriter<FileUploadJob> _writer;
    private readonly ChannelReader<FileUploadJob> _reader;
    private readonly ILogger<StreamingFileUploadService> _logger;
    private readonly FileUploadOptions _options;
    private readonly SemaphoreSlim _processingSlot;
    private readonly CancellationTokenSource _cancellationTokenSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingFileUploadService"/> class.
    /// </summary>
    /// <param name="options">File upload options.</param>
    /// <param name="logger">Logger instance.</param>
    public StreamingFileUploadService(
        IOptions<FileUploadOptions> options,
        ILogger<StreamingFileUploadService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _cancellationTokenSource = new CancellationTokenSource();

        // Create bounded channel for upload queue
        var channelOptions = new BoundedChannelOptions(_options.MaxConcurrentUploads)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };

        _uploadQueue = Channel.CreateBounded<FileUploadJob>(channelOptions);
        _writer = _uploadQueue.Writer;
        _reader = _uploadQueue.Reader;

        // Limit concurrent upload processing
        _processingSlot = new SemaphoreSlim(_options.MaxConcurrentUploads, _options.MaxConcurrentUploads);
    }

    /// <summary>
    /// Queues a file upload for processing with backpressure.
    /// </summary>
    /// <param name="uploadJob">The file upload job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the upload operation.</returns>
    public async Task<FileUploadResult> QueueFileUploadAsync(
        FileUploadJob uploadJob,
        CancellationToken cancellationToken = default)
    {
        using var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _cancellationTokenSource.Token);

        try
        {
            // Check queue capacity
            if (_uploadQueue.Reader.Count >= _options.MaxQueuedUploads)
            {
                _logger.LogWarning("Upload queue is full, rejecting upload for {FileName}", uploadJob.FileName);
                return FileUploadResult.Failure("Upload queue is full. Please try again later.");
            }

            // Wait for processing slot
            if (!await _processingSlot.WaitAsync(TimeSpan.FromSeconds(30), combinedToken.Token))
            {
                _logger.LogWarning("Timeout waiting for upload processing slot for {FileName}", uploadJob.FileName);
                return FileUploadResult.Failure("Upload service is busy. Please try again later.");
            }

            try
            {
                // Add to upload queue
                await _writer.WriteAsync(uploadJob, combinedToken.Token);

                // Process the upload
                return await ProcessFileUploadAsync(uploadJob, combinedToken.Token);
            }
            finally
            {
                _processingSlot.Release();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("File upload cancelled for {FileName}", uploadJob.FileName);
            return FileUploadResult.Failure("Upload was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process file upload for {FileName}", uploadJob.FileName);
            return FileUploadResult.Failure("Upload processing failed.");
        }
    }

    /// <summary>
    /// Processes a file upload with streaming to avoid memory exhaustion.
    /// </summary>
    /// <param name="uploadJob">The upload job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Upload result.</returns>
    private async Task<FileUploadResult> ProcessFileUploadAsync(
        FileUploadJob uploadJob,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var tempFilePath = Path.GetTempFileName();

        try
        {
            _logger.LogInformation(
                "Starting streaming upload for {FileName} ({FileSize} bytes)",
                uploadJob.FileName,
                uploadJob.ContentLength);

            // Stream file to disk with size validation
            await StreamFileToDiskAsync(uploadJob.InputStream, tempFilePath, uploadJob.ContentLength, cancellationToken);

            // Validate file size after streaming
            var fileInfo = new FileInfo(tempFilePath);
            if (fileInfo.Length > _options.MaxFileSizeBytes)
            {
                return FileUploadResult.Failure($"File size ({fileInfo.Length} bytes) exceeds maximum allowed size ({_options.MaxFileSizeBytes} bytes)");
            }

            // Create staged file
            var stagedFile = new StagedImportFile
            {
                LocalFilePath = tempFilePath,
                FileName = uploadJob.FileName,
                ContentType = uploadJob.ContentType,
                SizeBytes = fileInfo.Length
            };

            var processingTime = DateTimeOffset.UtcNow - startTime;
            _logger.LogInformation(
                "File upload completed for {FileName} in {ProcessingTime}ms",
                uploadJob.FileName,
                processingTime.TotalMilliseconds);

            return FileUploadResult.Success(stagedFile);
        }
        catch (Exception)
        {
            // Clean up temp file on failure
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch (Exception deleteEx)
            {
                _logger.LogWarning(deleteEx, "Failed to delete temp file {TempFilePath}", tempFilePath);
            }

            throw;
        }
    }

    /// <summary>
    /// Streams file content to disk with size validation and cancellation support.
    /// </summary>
    /// <param name="inputStream">Input stream.</param>
    /// <param name="filePath">Target file path.</param>
    /// <param name="expectedSize">Expected file size for validation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the streaming operation.</returns>
    private async Task StreamFileToDiskAsync(
        Stream inputStream,
        string filePath,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920);

        var buffer = new byte[81920]; // 80KB buffer
        long totalBytesRead = 0;
        int bytesRead;

        while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            totalBytesRead += bytesRead;

            // Check for size exceeded during streaming
            if (totalBytesRead > _options.MaxFileSizeBytes)
            {
                throw new InvalidOperationException($"File size exceeded maximum limit of {_options.MaxFileSizeBytes} bytes");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        // Validate final size matches expected
        if (expectedSize > 0 && totalBytesRead != expectedSize)
        {
            throw new InvalidOperationException($"File size mismatch. Expected {expectedSize} bytes, received {totalBytesRead} bytes");
        }

        await fileStream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Gets current queue metrics.
    /// </summary>
    /// <returns>Upload queue metrics.</returns>
    public UploadQueueMetrics GetQueueMetrics()
    {
        var snapshot = GetQueueSnapshot();

        return new UploadQueueMetrics
        {
            QueueDepth = snapshot.QueueDepth,
            MaxQueueDepth = snapshot.MaxQueueDepth,
            ActiveUploads = snapshot.ActiveUploads,
            MaxConcurrentUploads = snapshot.MaxConcurrentUploads
        };
    }

    /// <inheritdoc />
    public UploadQueueSnapshot GetQueueSnapshot()
    {
        return new UploadQueueSnapshot(
            _uploadQueue.Reader.Count,
            _options.MaxQueuedUploads,
            _options.MaxConcurrentUploads - _processingSlot.CurrentCount,
            _options.MaxConcurrentUploads);
    }

    /// <summary>
    /// Disposes the service and releases resources.
    /// </summary>
    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _writer.TryComplete();
        _processingSlot.Dispose();
        _cancellationTokenSource.Dispose();
    }
}

/// <summary>
/// File upload job information.
/// </summary>
internal sealed class FileUploadJob
{
    /// <summary>
    /// Gets or sets the input stream for the file.
    /// </summary>
    public required Stream InputStream { get; set; }

    /// <summary>
    /// Gets or sets the file name.
    /// </summary>
    public required string FileName { get; set; }

    /// <summary>
    /// Gets or sets the content type.
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Gets or sets the content length.
    /// </summary>
    public long ContentLength { get; set; }
}

/// <summary>
/// File upload result.
/// </summary>
internal sealed class FileUploadResult
{
    /// <summary>
    /// Gets a value indicating whether the upload was successful.
    /// </summary>
    public bool IsSuccess { get; private set; }

    /// <summary>
    /// Gets the staged file if successful.
    /// </summary>
    public StagedImportFile? StagedFile { get; private set; }

    /// <summary>
    /// Gets the error message if failed.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Creates a successful upload result.
    /// </summary>
    /// <param name="stagedFile">The staged file.</param>
    /// <returns>Success result.</returns>
    public static FileUploadResult Success(StagedImportFile stagedFile)
    {
        return new FileUploadResult
        {
            IsSuccess = true,
            StagedFile = stagedFile
        };
    }

    /// <summary>
    /// Creates a failed upload result.
    /// </summary>
    /// <param name="errorMessage">The error message.</param>
    /// <returns>Failure result.</returns>
    public static FileUploadResult Failure(string errorMessage)
    {
        return new FileUploadResult
        {
            IsSuccess = false,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Upload queue metrics.
/// </summary>
internal sealed class UploadQueueMetrics
{
    /// <summary>
    /// Gets or sets the current queue depth.
    /// </summary>
    public required int QueueDepth { get; set; }

    /// <summary>
    /// Gets or sets the maximum queue depth.
    /// </summary>
    public required int MaxQueueDepth { get; set; }

    /// <summary>
    /// Gets or sets the number of active uploads.
    /// </summary>
    public required int ActiveUploads { get; set; }

    /// <summary>
    /// Gets or sets the maximum concurrent uploads.
    /// </summary>
    public required int MaxConcurrentUploads { get; set; }
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

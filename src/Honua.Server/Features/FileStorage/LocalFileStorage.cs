// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.FileStorage;

/// <summary>
/// Local filesystem implementation of cloud file storage for development and testing
/// </summary>
internal sealed class LocalFileStorage : ICloudFileStorage
{
    private readonly LocalStorageOptions _options;
    private readonly ILogger<LocalFileStorage> _logger;
    private readonly IUploadProgressStore _progressStore;
    private readonly ConcurrentDictionary<string, CloudFile> _fileIndex;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _batchIndex;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _uploadCancellationTokens;
    private readonly string _basePath;
    private readonly string _metadataPath;

    /// <summary>
    /// Creates a new local file storage instance
    /// </summary>
    /// <param name="options">Local storage options</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="progressStore">Upload progress store</param>
    public LocalFileStorage(
        IOptions<LocalStorageOptions> options,
        ILogger<LocalFileStorage> logger,
        IUploadProgressStore progressStore)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _basePath = string.IsNullOrWhiteSpace(_options.BasePath)
            ? Directory.GetCurrentDirectory()
            : Path.TrimEndingDirectorySeparator(_options.BasePath);
        _metadataPath = Path.Combine(_basePath, ".metadata");
        _fileIndex = new ConcurrentDictionary<string, CloudFile>();
        _batchIndex = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
        _uploadCancellationTokens = new ConcurrentDictionary<string, CancellationTokenSource>();

        InitializeStorage();
    }

    /// <inheritdoc />
    public CloudStorageProvider Provider => CloudStorageProvider.Local;

    /// <inheritdoc />
    public async Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var uploadId = request.UploadId;
        var linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Store cancellation token for potential cancellation
        _uploadCancellationTokens[uploadId] = linkedCancellationSource;

        try
        {
            var totalBytes = request.SizeBytes ?? request.Content.Length;
            var initialProgress = UploadProgress.CreateInitial(uploadId, request.FileName, totalBytes, request.ContentType);

            // Store initial progress
            await _progressStore.SetProgressAsync(uploadId, initialProgress, TimeSpan.FromHours(1), cancellationToken);

            // Report initial progress if callback provided
            request.Progress?.Report(initialProgress);

            var fileId = GenerateFileId();
            var storagePath = BuildStoragePath(fileId, request.FileName, request.Folder);
            var fullPath = Path.Combine(_basePath, storagePath);

            // Ensure directory exists
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write file content with progress tracking
            string? contentHash;
            long sizeBytes;

            await using (var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var hashAlgorithm = SHA256.Create();
                await using var cryptoStream = new CryptoStream(fileStream, hashAlgorithm, CryptoStreamMode.Write, leaveOpen: true);

                // Wrap the source stream with progress tracking if progress callback provided
                Stream sourceStream = request.Content;
                if (request.Progress != null)
                {
                    var progressTracker = new Progress<UploadProgress>(async progress =>
                    {
                        await _progressStore.SetProgressAsync(uploadId, progress, TimeSpan.FromHours(1), CancellationToken.None);
                        request.Progress.Report(progress);
                    });
                    sourceStream = new ProgressTrackingStream(request.Content, progressTracker, uploadId, request.FileName, totalBytes);
                }

                await sourceStream.CopyToAsync(cryptoStream, linkedCancellationSource.Token);
                await cryptoStream.FlushFinalBlockAsync(linkedCancellationSource.Token);

                if (sourceStream != request.Content)
                {
                    await sourceStream.DisposeAsync();
                }

                contentHash = Convert.ToHexString(hashAlgorithm.Hash ?? []);
                sizeBytes = fileStream.Length;
            }

            var uploadedAt = DateTimeOffset.UtcNow;
            var expiresAt = request.TimeToLive.HasValue
                ? uploadedAt.Add(request.TimeToLive.Value)
                : (DateTimeOffset?)null;

            var cloudFile = new CloudFile
            {
                FileId = fileId,
                FileName = request.FileName,
                StoragePath = storagePath,
                ContentType = request.ContentType,
                SizeBytes = sizeBytes,
                UploadedAt = uploadedAt,
                ExpiresAt = expiresAt,
                ContentHash = contentHash,
                Metadata = request.Metadata.Add("UploadId", uploadId),
                Provider = CloudStorageProvider.Local
            };

            _fileIndex[fileId] = cloudFile;
            await SaveMetadataAsync(cloudFile, cancellationToken);

            // Report completion
            var completedProgress = new UploadProgress
            {
                UploadId = uploadId,
                Status = OperationStatus.Completed,
                BytesUploaded = sizeBytes,
                TotalBytes = totalBytes,
                FileName = request.FileName,
                ContentType = request.ContentType,
                CloudFileId = fileId,
                StartedAt = initialProgress.StartedAt,
                CompletedAt = uploadedAt,
                CurrentPhase = "Upload completed"
            };
            await _progressStore.SetProgressAsync(uploadId, completedProgress, TimeSpan.FromHours(1), cancellationToken);
            request.Progress?.Report(completedProgress);

            stopwatch.Stop();
            FileStorageLog.FileUploaded(
                _logger,
                request.FileName,
                sizeBytes,
                fileId,
                stopwatch.ElapsedMilliseconds);

            return UploadResult.CreateSuccess(cloudFile, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (linkedCancellationSource.Token.IsCancellationRequested)
        {
            stopwatch.Stop();
            var cancelledProgress = new UploadProgress
            {
                UploadId = uploadId,
                Status = OperationStatus.Cancelled,
                BytesUploaded = 0,
                TotalBytes = request.SizeBytes ?? 0,
                FileName = request.FileName,
                ContentType = request.ContentType,
                StartedAt = DateTimeOffset.UtcNow - stopwatch.Elapsed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Upload was cancelled",
                CurrentPhase = "Cancelled"
            };
            await _progressStore.SetProgressAsync(uploadId, cancelledProgress, TimeSpan.FromMinutes(10), CancellationToken.None);
            request.Progress?.Report(cancelledProgress);
            throw;
        }
        catch (ArgumentException ex) when (string.Equals(ex.ParamName, "folder", StringComparison.Ordinal))
        {
            stopwatch.Stop();
            var failedProgress = new UploadProgress
            {
                UploadId = uploadId,
                Status = OperationStatus.Failed,
                BytesUploaded = 0,
                TotalBytes = request.SizeBytes ?? 0,
                FileName = request.FileName,
                ContentType = request.ContentType,
                StartedAt = DateTimeOffset.UtcNow - stopwatch.Elapsed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = ex.Message,
                CurrentPhase = "Failed"
            };
            await _progressStore.SetProgressAsync(uploadId, failedProgress, TimeSpan.FromMinutes(10), CancellationToken.None);
            request.Progress?.Report(failedProgress);
            FileStorageLog.FileUploadFailed(_logger, ex, request.FileName);
            return UploadResult.CreateFailure(ex.Message, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var failedProgress = new UploadProgress
            {
                UploadId = uploadId,
                Status = OperationStatus.Failed,
                BytesUploaded = 0,
                TotalBytes = request.SizeBytes ?? 0,
                FileName = request.FileName,
                ContentType = request.ContentType,
                StartedAt = DateTimeOffset.UtcNow - stopwatch.Elapsed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "File upload failed",
                CurrentPhase = "Failed"
            };
            await _progressStore.SetProgressAsync(uploadId, failedProgress, TimeSpan.FromMinutes(10), CancellationToken.None);
            request.Progress?.Report(failedProgress);
            FileStorageLog.FileUploadFailed(_logger, ex, request.FileName);
            return UploadResult.CreateFailure("File upload failed.", stopwatch.Elapsed);
        }
        finally
        {
            // Clean up cancellation token
            _uploadCancellationTokens.TryRemove(uploadId, out _);
            linkedCancellationSource.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var stream = new MemoryStream(request.Content);
        return await UploadAsync(new FileUploadRequest
        {
            Content = stream,
            FileName = request.FileName,
            ContentType = request.ContentType,
            SizeBytes = request.Content.Length,
            TimeToLive = request.TimeToLive,
            Metadata = request.Metadata,
            Folder = request.Folder,
            UploadId = Guid.NewGuid().ToString() // Generate new upload ID for byte array uploads
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        return _progressStore.GetProgressAsync(uploadId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);

        if (_uploadCancellationTokens.TryRemove(uploadId, out var cancellationSource))
        {
            await cancellationSource.CancelAsync();
            cancellationSource.Dispose();

            // Update progress to cancelled status
            var currentProgress = await _progressStore.GetProgressAsync(uploadId, cancellationToken);
            if (currentProgress != null && currentProgress.Status == OperationStatus.Processing)
            {
                var cancelledProgress = currentProgress with
                {
                    Status = OperationStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Cancelled"
                };
                await _progressStore.SetProgressAsync(uploadId, cancelledProgress, TimeSpan.FromMinutes(10), cancellationToken);
            }

            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
    {
        return _progressStore.GetActiveUploadsAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        var cloudFile = await GetMetadataAsync(fileId, cancellationToken);
        if (cloudFile is null)
        {
            return null;
        }

        var fullPath = Path.Combine(_basePath, cloudFile.StoragePath);
        if (!File.Exists(fullPath))
        {
            FileStorageLog.FileMissingOnDisk(_logger, fileId);
            return null;
        }

        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    }

    /// <inheritdoc />
    public async Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default)
    {
        await using var stream = await DownloadAsync(fileId, cancellationToken);
        if (stream is null)
        {
            return null;
        }

        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken);
        return memoryStream.ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        if (!_fileIndex.TryRemove(fileId, out var cloudFile))
        {
            return false;
        }

        var fullPath = Path.Combine(_basePath, cloudFile.StoragePath);
        var metadataFile = GetMetadataFilePath(fileId);

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            if (File.Exists(metadataFile))
            {
                File.Delete(metadataFile);
            }

            // Remove from batch index if present
            foreach (var batch in _batchIndex)
            {
                _ = batch.Value.TryRemove(fileId, out _);
            }

            // Clean up upload progress if exists
            if (cloudFile.Metadata.TryGetValue("UploadId", out var uploadId))
            {
                await _progressStore.DeleteProgressAsync(uploadId, cancellationToken);
            }

            FileStorageLog.FileDeleted(_logger, fileId);
            return true;
        }
        catch (Exception ex)
        {
            FileStorageLog.FileDeleteFailed(_logger, ex, fileId);
            // Re-add to index since deletion failed
            _fileIndex[fileId] = cloudFile;
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var batchId = Guid.NewGuid().ToString("N");
        var uploadedFiles = new List<CloudFile>();
        var failedFiles = new Dictionary<string, string>();

        _batchIndex[batchId] = new ConcurrentDictionary<string, byte>();

        foreach (var file in request.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = file.Metadata.Add("BatchId", batchId);
            var result = await UploadAsync(new FileUploadRequest
            {
                Content = file.Content,
                FileName = file.FileName,
                ContentType = file.ContentType,
                SizeBytes = file.SizeBytes,
                TimeToLive = request.TimeToLive,
                Metadata = metadata,
                Folder = request.Folder ?? batchId,
                UploadId = Guid.NewGuid().ToString()
            }, cancellationToken);

            if (result.Success && result.File is not null)
            {
                uploadedFiles.Add(result.File);
                _ = _batchIndex[batchId].TryAdd(result.File.FileId, 0);
            }
            else
            {
                failedFiles[file.FileName] = result.ErrorMessage ?? "Unknown error";

                if (!request.ContinueOnError)
                {
                    // Rollback uploaded files
                    foreach (var uploaded in uploadedFiles)
                    {
                        await DeleteAsync(uploaded.FileId, cancellationToken);
                    }

                    stopwatch.Stop();
                    return BatchUploadResult.CreateFailure(batchId, failedFiles, stopwatch.Elapsed);
                }
            }
        }

        stopwatch.Stop();

        if (failedFiles.Count > 0)
        {
            return BatchUploadResult.CreatePartialSuccess(batchId, uploadedFiles, failedFiles, stopwatch.Elapsed);
        }

        return BatchUploadResult.CreateSuccess(batchId, uploadedFiles, stopwatch.Elapsed);
    }

    /// <inheritdoc />
    public async Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);

        if (!_batchIndex.TryRemove(batchId, out var fileIds))
        {
            return 0;
        }

        var deletedCount = 0;
        foreach (var fileId in fileIds.Keys)
        {
            if (await DeleteAsync(fileId, cancellationToken))
            {
                deletedCount++;
            }
        }

        FileStorageLog.BatchDeleted(_logger, batchId, deletedCount);
        return deletedCount;
    }

    /// <inheritdoc />
    public Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        _fileIndex.TryGetValue(fileId, out var cloudFile);
        return Task.FromResult(cloudFile);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        var exists = _fileIndex.ContainsKey(fileId);
        return Task.FromResult(exists);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CloudFile>> ListFilesAsync(
        string? folder = null,
        int maxResults = 1000,
        CancellationToken cancellationToken = default)
    {
        var files = _fileIndex.Values
            .Where(f => string.IsNullOrEmpty(folder) || f.StoragePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.UploadedAt)
            .Take(maxResults)
            .ToList();

        return Task.FromResult<IReadOnlyList<CloudFile>>(files);
    }

    /// <inheritdoc />
    public Task<string?> GetPresignedUrlAsync(
        string fileId,
        TimeSpan? expiresIn = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        // For local storage, we return the file path as a "URL"
        // In production, cloud providers would return actual presigned URLs
        if (!_fileIndex.TryGetValue(fileId, out var cloudFile))
        {
            return Task.FromResult<string?>(null);
        }

        var fullPath = Path.Combine(_basePath, cloudFile.StoragePath);
        return Task.FromResult<string?>(new Uri(fullPath).AbsoluteUri);
    }

    /// <inheritdoc />
    public Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
        string fileName,
        string contentType,
        TimeSpan? expiresIn = null,
        string? folder = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        // For local storage, we generate a file ID and path
        // Real cloud implementations would provide presigned upload URLs
        var fileId = GenerateFileId();
        var storagePath = BuildStoragePath(fileId, fileName, folder);
        var fullPath = Path.Combine(_basePath, storagePath);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return Task.FromResult<(string Url, string FileId)?>(
            (new Uri(fullPath).AbsoluteUri, fileId));
    }

    /// <inheritdoc />
    public async Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expiredFiles = _fileIndex.Values
            .Where(f => f.ExpiresAt.HasValue && f.ExpiresAt.Value <= now)
            .ToList();

        var cleanedCount = 0;
        foreach (var file in expiredFiles)
        {
            if (await DeleteAsync(file.FileId, cancellationToken))
            {
                cleanedCount++;
            }
        }

        if (cleanedCount > 0)
        {
            FileStorageLog.ExpiredFilesCleaned(_logger, cleanedCount);
        }

        return cleanedCount;
    }

    private void InitializeStorage()
    {
        if (_options.CreateDirectoryIfNotExists && !Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
            FileStorageLog.StorageDirectoryCreated(_logger, _basePath);
        }

        if (!Directory.Exists(_metadataPath))
        {
            Directory.CreateDirectory(_metadataPath);
        }

        // Load existing metadata
        LoadExistingMetadata();
    }

    private void LoadExistingMetadata()
    {
        if (!Directory.Exists(_metadataPath))
        {
            return;
        }

        var metadataFiles = Directory.GetFiles(_metadataPath, "*.json");
        foreach (var file in metadataFiles)
        {
            try
            {
                var json = File.ReadAllText(file);
                var cloudFile = JsonSerializer.Deserialize(json, FileStorageJsonContext.Default.CloudFile);
                if (cloudFile is not null)
                {
                    _fileIndex[cloudFile.FileId] = cloudFile;

                    // Rebuild batch index
                    if (cloudFile.Metadata.TryGetValue("BatchId", out var batchId))
                    {
                        var batchFiles = _batchIndex.GetOrAdd(batchId, _ => new ConcurrentDictionary<string, byte>());
                        _ = batchFiles.TryAdd(cloudFile.FileId, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                FileStorageLog.MetadataLoadFailed(_logger, ex, file);
            }
        }

        FileStorageLog.MetadataLoaded(_logger, _fileIndex.Count);
    }

    private async Task SaveMetadataAsync(CloudFile cloudFile, CancellationToken cancellationToken)
    {
        var metadataFile = GetMetadataFilePath(cloudFile.FileId);
        var json = JsonSerializer.Serialize(cloudFile, FileStorageJsonContext.Default.CloudFile);
        await File.WriteAllTextAsync(metadataFile, json, cancellationToken);
    }

    private string GetMetadataFilePath(string fileId) =>
        Path.Combine(_metadataPath, $"{fileId}.json");

    private static string GenerateFileId() =>
        Guid.NewGuid().ToString("N");

    private string BuildStoragePath(string fileId, string fileName, string? folder)
    {
        var extension = Path.GetExtension(fileName);
        var storageName = $"{fileId}{extension}";

        if (string.IsNullOrWhiteSpace(folder))
        {
            return storageName;
        }

        ValidateFolderPath(folder);

        return Path.Combine(folder, storageName);
    }

    private static void ValidateFolderPath(string folder)
    {
        if (Path.IsPathRooted(folder))
        {
            throw new ArgumentException("Folder must be a relative path.", nameof(folder));
        }

        if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException("Folder contains invalid path characters.", nameof(folder));
        }

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var segments = folder.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new ArgumentException("Folder must not contain relative traversal segments.", nameof(folder));
        }
    }
}

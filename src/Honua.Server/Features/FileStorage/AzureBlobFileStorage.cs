// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.FileStorage;

internal sealed class AzureBlobFileStorage : ICloudFileStorage
{
    private static readonly TimeSpan _defaultSignedUrlLifetime = TimeSpan.FromMinutes(15);

    private readonly AzureBlobOptions _options;
    private readonly BlobContainerClient _containerClient;
    private readonly ILogger<AzureBlobFileStorage> _logger;
    private readonly IUploadProgressStore _progressStore;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _batchIndex;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _uploadCancellationTokens;

    public AzureBlobFileStorage(
        IOptions<CloudStorageOptions> options,
        ILogger<AzureBlobFileStorage> logger,
        IUploadProgressStore progressStore)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));

        var resolved = options.Value ?? throw new ArgumentNullException(nameof(options));
        _options = resolved.AzureBlob ?? throw new InvalidOperationException("Azure Blob options are not configured.");

        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new InvalidOperationException("Azure Blob connection string is required.");
        }

        if (string.IsNullOrWhiteSpace(_options.ContainerName))
        {
            throw new InvalidOperationException("Azure Blob container name is required.");
        }

        _containerClient = new BlobContainerClient(_options.ConnectionString, _options.ContainerName);
        _containerClient.CreateIfNotExists();
        _batchIndex = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
        _uploadCancellationTokens = new ConcurrentDictionary<string, CancellationTokenSource>();
    }

    public CloudStorageProvider Provider => CloudStorageProvider.AzureBlob;

    public async Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var uploadId = request.UploadId;
        var linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var totalBytes = request.SizeBytes ?? (request.Content.CanSeek ? request.Content.Length : 0);

        _uploadCancellationTokens[uploadId] = linkedCancellationSource;

        var initialProgress = UploadProgress.CreateInitial(uploadId, request.FileName, totalBytes, request.ContentType);
        await _progressStore.SetProgressAsync(uploadId, initialProgress, TimeSpan.FromHours(1), cancellationToken);
        request.Progress?.Report(initialProgress);

        try
        {
            var objectKey = CloudStoragePath.BuildObjectKey(
                CloudStoragePath.GenerateFileId(),
                request.FileName,
                request.Folder,
                _options.BlobPrefix);

            var uploadedAt = DateTimeOffset.UtcNow;
            var expiresAt = request.TimeToLive.HasValue
                ? uploadedAt.Add(request.TimeToLive.Value)
                : (DateTimeOffset?)null;

            var metadata = CloudStorageMetadata.BuildMetadata(request.Metadata, request.FileName, expiresAt);
            var blobClient = _containerClient.GetBlobClient(objectKey);

            var uploadOptions = new BlobUploadOptions
            {
                Metadata = metadata,
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = request.ContentType
                }
            };

            if (request.Progress != null)
            {
                var lastProgressUpdate = DateTimeOffset.UtcNow;
                uploadOptions.ProgressHandler = new Progress<long>(bytesTransferred =>
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - lastProgressUpdate < TimeSpan.FromMilliseconds(100) && bytesTransferred < totalBytes)
                    {
                        return;
                    }

                    var progress = new UploadProgress
                    {
                        UploadId = uploadId,
                        Status = OperationStatus.Processing,
                        BytesUploaded = bytesTransferred,
                        TotalBytes = totalBytes,
                        FileName = request.FileName,
                        ContentType = request.ContentType,
                        StartedAt = initialProgress.StartedAt,
                        CurrentPhase = "Uploading"
                    };

                    _ = _progressStore.SetProgressAsync(uploadId, progress, TimeSpan.FromHours(1), CancellationToken.None);
                    request.Progress.Report(progress);
                    lastProgressUpdate = now;
                });
            }

            await blobClient.UploadAsync(request.Content, uploadOptions, linkedCancellationSource.Token);

            var sizeBytes = await ResolveSizeAsync(blobClient, request, cancellationToken);
            var cloudFile = new CloudFile
            {
                FileId = objectKey,
                FileName = request.FileName,
                StoragePath = objectKey,
                ContentType = request.ContentType,
                SizeBytes = sizeBytes,
                UploadedAt = uploadedAt,
                ExpiresAt = expiresAt,
                ContentHash = null,
                Metadata = request.Metadata,
                Provider = CloudStorageProvider.AzureBlob
            };

            var completedProgress = new UploadProgress
            {
                UploadId = uploadId,
                Status = OperationStatus.Completed,
                BytesUploaded = sizeBytes,
                TotalBytes = totalBytes,
                FileName = request.FileName,
                ContentType = request.ContentType,
                CloudFileId = objectKey,
                StartedAt = initialProgress.StartedAt,
                CompletedAt = uploadedAt,
                CurrentPhase = "Upload completed"
            };
            await _progressStore.SetProgressAsync(uploadId, completedProgress, TimeSpan.FromHours(1), cancellationToken);
            request.Progress?.Report(completedProgress);

            stopwatch.Stop();
            FileStorageLog.FileUploaded(_logger, request.FileName, sizeBytes, objectKey, stopwatch.ElapsedMilliseconds);

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
                TotalBytes = totalBytes,
                FileName = request.FileName,
                ContentType = request.ContentType,
                StartedAt = initialProgress.StartedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Upload was cancelled",
                CurrentPhase = "Cancelled"
            };
            await _progressStore.SetProgressAsync(uploadId, cancelledProgress, TimeSpan.FromMinutes(10), CancellationToken.None);
            request.Progress?.Report(cancelledProgress);
            throw;
        }
        catch (ArgumentException ex)
        {
            stopwatch.Stop();
            var failedProgress = new UploadProgress
            {
                UploadId = uploadId,
                Status = OperationStatus.Failed,
                BytesUploaded = 0,
                TotalBytes = totalBytes,
                FileName = request.FileName,
                ContentType = request.ContentType,
                StartedAt = initialProgress.StartedAt,
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
                TotalBytes = totalBytes,
                FileName = request.FileName,
                ContentType = request.ContentType,
                StartedAt = initialProgress.StartedAt,
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
            _uploadCancellationTokens.TryRemove(uploadId, out _);
            linkedCancellationSource.Dispose();
        }
    }

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
            Folder = request.Folder
        }, cancellationToken);
    }

    public async Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        try
        {
            var blobClient = _containerClient.GetBlobClient(fileId);
            var response = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
            return response.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

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

    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        try
        {
            var blobClient = _containerClient.GetBlobClient(fileId);
            var deleted = await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            if (deleted.Value)
            {
                FileStorageLog.FileDeleted(_logger, fileId);
            }

            return deleted.Value;
        }
        catch (RequestFailedException ex)
        {
            FileStorageLog.FileDeleteFailed(_logger, ex, fileId);
            return false;
        }
    }

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
                Folder = request.Folder ?? batchId
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

    public async Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);

        if (_batchIndex.TryRemove(batchId, out var fileIds))
        {
            var deletedCount = 0;
            foreach (var fileId in fileIds.Keys)
            {
                if (await DeleteAsync(fileId, cancellationToken))
                {
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                FileStorageLog.BatchDeleted(_logger, batchId, deletedCount);
            }

            return deletedCount;
        }

        return await DeleteByPrefixAsync(batchId, cancellationToken);
    }

    public async Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        try
        {
            var blobClient = _containerClient.GetBlobClient(fileId);
            var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            var metadata = properties.Value.Metadata;
            var fileName = CloudStorageMetadata.GetFileName(metadata) ?? CloudStoragePath.GetFileNameFromKey(fileId);
            var expiresAt = CloudStorageMetadata.GetExpiresAt(metadata);

            return new CloudFile
            {
                FileId = fileId,
                FileName = fileName,
                StoragePath = fileId,
                ContentType = properties.Value.ContentType ?? "application/octet-stream",
                SizeBytes = properties.Value.ContentLength,
                UploadedAt = properties.Value.LastModified,
                ExpiresAt = expiresAt,
                ContentHash = null,
                Metadata = CloudStorageMetadata.ExtractUserMetadata(metadata),
                Provider = CloudStorageProvider.AzureBlob
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        var blobClient = _containerClient.GetBlobClient(fileId);
        var exists = await blobClient.ExistsAsync(cancellationToken);
        return exists.Value;
    }

    public async Task<IReadOnlyList<CloudFile>> ListFilesAsync(
        string? folder = null,
        int maxResults = 1000,
        CancellationToken cancellationToken = default)
    {
        var prefix = CloudStoragePath.BuildPrefix(folder, _options.BlobPrefix);
        var results = new List<CloudFile>();

        await foreach (var blobItem in _containerClient.GetBlobsAsync(
                           BlobTraits.Metadata,
                           BlobStates.None,
                           prefix,
                           cancellationToken))
        {
            var metadata = blobItem.Metadata ?? new Dictionary<string, string>();
            var fileName = CloudStorageMetadata.GetFileName(metadata) ?? CloudStoragePath.GetFileNameFromKey(blobItem.Name);
            var expiresAt = CloudStorageMetadata.GetExpiresAt(metadata);

            results.Add(new CloudFile
            {
                FileId = blobItem.Name,
                FileName = fileName,
                StoragePath = blobItem.Name,
                ContentType = blobItem.Properties.ContentType ?? "application/octet-stream",
                SizeBytes = blobItem.Properties.ContentLength ?? 0,
                UploadedAt = blobItem.Properties.LastModified ?? DateTimeOffset.UtcNow,
                ExpiresAt = expiresAt,
                ContentHash = null,
                Metadata = CloudStorageMetadata.ExtractUserMetadata(metadata),
                Provider = CloudStorageProvider.AzureBlob
            });

            if (results.Count >= maxResults)
            {
                break;
            }
        }

        return results;
    }

    public async Task<string?> GetPresignedUrlAsync(
        string fileId,
        TimeSpan? expiresIn = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        var blobClient = _containerClient.GetBlobClient(fileId);
        if (!blobClient.CanGenerateSasUri)
        {
            return null;
        }

        var exists = await blobClient.ExistsAsync(cancellationToken);
        if (!exists.Value)
        {
            return null;
        }

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = _options.ContainerName,
            BlobName = fileId,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(expiresIn ?? _defaultSignedUrlLifetime)
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        return blobClient.GenerateSasUri(sasBuilder).ToString();
    }

    public Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
        string fileName,
        string contentType,
        TimeSpan? expiresIn = null,
        string? folder = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        cancellationToken.ThrowIfCancellationRequested();

        var objectKey = CloudStoragePath.BuildObjectKey(
            CloudStoragePath.GenerateFileId(),
            fileName,
            folder,
            _options.BlobPrefix);

        var blobClient = _containerClient.GetBlobClient(objectKey);
        if (!blobClient.CanGenerateSasUri)
        {
            return Task.FromResult<(string Url, string FileId)?>(null);
        }

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = _options.ContainerName,
            BlobName = objectKey,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(expiresIn ?? _defaultSignedUrlLifetime)
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

        var url = blobClient.GenerateSasUri(sasBuilder).ToString();
        return Task.FromResult<(string Url, string FileId)?>((url, objectKey));
    }

    public async Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var prefix = CloudStoragePath.BuildPrefix(null, _options.BlobPrefix);
        var cleanedCount = 0;

        await foreach (var blobItem in _containerClient.GetBlobsAsync(
                           BlobTraits.Metadata,
                           BlobStates.None,
                           prefix,
                           cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expiresAt = CloudStorageMetadata.GetExpiresAt(blobItem.Metadata);
            if (expiresAt.HasValue && expiresAt.Value <= now)
            {
                var blobClient = _containerClient.GetBlobClient(blobItem.Name);
                var deleted = await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
                if (deleted.Value)
                {
                    cleanedCount++;
                }
            }
        }

        if (cleanedCount > 0)
        {
            FileStorageLog.ExpiredFilesCleaned(_logger, cleanedCount);
        }

        return cleanedCount;
    }

    /// <inheritdoc />
    public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        return _progressStore.GetProgressAsync(uploadId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        return CancelUploadInternalAsync(uploadId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
    {
        return _progressStore.GetActiveUploadsAsync(cancellationToken);
    }

    private async Task<bool> CancelUploadInternalAsync(string uploadId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);

        if (_uploadCancellationTokens.TryRemove(uploadId, out var cancellationSource))
        {
            await cancellationSource.CancelAsync();
            cancellationSource.Dispose();

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

    private async Task<long> ResolveSizeAsync(
        BlobClient blobClient,
        FileUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SizeBytes.HasValue)
        {
            return request.SizeBytes.Value;
        }

        if (request.Content.CanSeek)
        {
            return request.Content.Length;
        }

        try
        {
            var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            return properties.Value.ContentLength;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return 0;
        }
    }

    private async Task<int> DeleteByPrefixAsync(string batchId, CancellationToken cancellationToken)
    {
        var prefix = CloudStoragePath.BuildPrefix(batchId, _options.BlobPrefix);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return 0;
        }

        var deletedCount = 0;
        await foreach (var blobItem in _containerClient.GetBlobsAsync(
                           BlobTraits.None,
                           BlobStates.None,
                           prefix,
                           cancellationToken))
        {
            var blobClient = _containerClient.GetBlobClient(blobItem.Name);
            var deleted = await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            if (deleted.Value)
            {
                deletedCount++;
            }
        }

        if (deletedCount > 0)
        {
            FileStorageLog.BatchDeleted(_logger, batchId, deletedCount);
        }

        return deletedCount;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

internal sealed class AzureBlobFileStorage : CloudFileStorageBase
{
    private readonly TimeSpan _signedUrlLifetime;

    private readonly AzureBlobOptions _options;
    private readonly BlobContainerClient _containerClient;

    public AzureBlobFileStorage(
        IOptions<CloudStorageOptions> options,
        ILogger<AzureBlobFileStorage> logger,
        IUploadProgressStore progressStore)
        : base(logger, progressStore)
    {
        ArgumentNullException.ThrowIfNull(options);

        var resolved = options.Value ?? throw new ArgumentNullException(nameof(options));
        _signedUrlLifetime = resolved.SignedUrlLifetime;
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
    }

    public override CloudStorageProvider Provider => CloudStorageProvider.AzureBlob;

    public override async Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var uploadId = request.UploadId;
        var linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var totalBytes = request.SizeBytes ?? (request.Content.CanSeek ? request.Content.Length : 0);

        UploadCancellationTokens[uploadId] = linkedCancellationSource;

        var initialProgress = UploadProgress.CreateInitial(uploadId, request.FileName, totalBytes, request.ContentType);
        await ProgressStore.SetProgressAsync(uploadId, initialProgress, TimeSpan.FromHours(1), cancellationToken);
        request.Progress?.Report(initialProgress);

        try
        {
            var objectKey = !string.IsNullOrWhiteSpace(request.ObjectKeyOverride)
                ? request.ObjectKeyOverride
                : CloudStoragePath.BuildObjectKey(
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

                    // Fire-and-forget for the throttled in-flight update, but observe the Task so a
                    // progress-store outage is logged rather than silently dropped (the awaited
                    // completed/failed writes already surface their own errors).
                    _ = ProgressStore.SetProgressAsync(uploadId, progress, TimeSpan.FromHours(1), CancellationToken.None)
                        .ContinueWith(
                            t => FileStorageLog.ProgressUpdateFailed(Logger, t.Exception!, uploadId),
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
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
            await ProgressStore.SetProgressAsync(uploadId, completedProgress, TimeSpan.FromHours(1), cancellationToken);
            request.Progress?.Report(completedProgress);

            stopwatch.Stop();
            FileStorageLog.FileUploaded(Logger, request.FileName, sizeBytes, objectKey, stopwatch.ElapsedMilliseconds);

            return UploadResult.CreateSuccess(cloudFile, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (linkedCancellationSource.Token.IsCancellationRequested)
        {
            stopwatch.Stop();
            await ReportCancelledUploadAsync(uploadId, request, totalBytes, initialProgress.StartedAt);
            throw;
        }
        catch (ArgumentException ex)
        {
            var (progressMessage, resultMessage) = ResolveArgumentFailureMessages(ex);
            return await ReportFailedUploadAsync(
                uploadId,
                request,
                stopwatch,
                totalBytes,
                initialProgress.StartedAt,
                ex,
                progressMessage,
                resultMessage);
        }
        catch (Exception ex)
        {
            return await ReportFailedUploadAsync(
                uploadId,
                request,
                stopwatch,
                totalBytes,
                initialProgress.StartedAt,
                ex,
                "File upload failed",
                "File upload failed.");
        }
        finally
        {
            UploadCancellationTokens.TryRemove(uploadId, out _);
            linkedCancellationSource.Dispose();
        }
    }

    public override async Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        try
        {
            var blobClient = _containerClient.GetBlobClient(fileId);
            var response = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
            // Wrap the content stream so that disposing it also disposes the BlobDownloadStreamingResult
            return new BlobDownloadStream(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Wrapper stream that ensures the BlobDownloadStreamingResult is disposed when the stream is disposed.
    /// </summary>
    private sealed class BlobDownloadStream : Stream
    {
        private readonly BlobDownloadStreamingResult _result;
        private readonly Stream _inner;

        public BlobDownloadStream(BlobDownloadStreamingResult result)
        {
            _result = result;
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
            if (disposing)
            {
                _inner.Dispose();
                _result.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _result.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }


    public override async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        try
        {
            var blobClient = _containerClient.GetBlobClient(fileId);
            var deleted = await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            if (deleted.Value)
            {
                FileStorageLog.FileDeleted(Logger, fileId);
            }

            return deleted.Value;
        }
        catch (RequestFailedException ex)
        {
            FileStorageLog.FileDeleteFailed(Logger, ex, fileId);
            return false;
        }
    }

    public override async Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
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

    public override async Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        var blobClient = _containerClient.GetBlobClient(fileId);
        var exists = await blobClient.ExistsAsync(cancellationToken);
        return exists.Value;
    }

    public override async Task<IReadOnlyList<CloudFile>> ListFilesAsync(
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

    public override async Task<string?> GetPresignedUrlAsync(
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
            ExpiresOn = DateTimeOffset.UtcNow.Add(expiresIn ?? _signedUrlLifetime)
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        return blobClient.GenerateSasUri(sasBuilder).ToString();
    }

    public override Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
        string fileName,
        string contentType,
        TimeSpan? expiresIn = null,
        string? folder = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePresignedUploadArguments(fileName, contentType, cancellationToken);

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
            ExpiresOn = DateTimeOffset.UtcNow.Add(expiresIn ?? _signedUrlLifetime)
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

        var url = blobClient.GenerateSasUri(sasBuilder).ToString();
        return Task.FromResult<(string Url, string FileId)?>((url, objectKey));
    }

    public override async Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
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
            FileStorageLog.ExpiredFilesCleaned(Logger, cleanedCount);
        }

        return cleanedCount;
    }

    private static async Task<long> ResolveSizeAsync(
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

    protected override async Task<int> DeleteByPrefixAsync(string batchId, CancellationToken cancellationToken)
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
            FileStorageLog.BatchDeleted(Logger, batchId, deletedCount);
        }

        return deletedCount;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.FileStorage;

internal sealed class AwsS3FileStorage : ICloudFileStorage
{
    private static readonly TimeSpan _defaultSignedUrlLifetime = TimeSpan.FromMinutes(15);

    private readonly AwsS3Options _options;
    private readonly AmazonS3Client _client;
    private readonly ILogger<AwsS3FileStorage> _logger;
    private readonly IUploadProgressStore _progressStore;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _batchIndex;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _uploadCancellationTokens;

    public AwsS3FileStorage(
        IOptions<CloudStorageOptions> options,
        ILogger<AwsS3FileStorage> logger,
        IUploadProgressStore progressStore)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));

        var resolved = options.Value ?? throw new ArgumentNullException(nameof(options));
        _options = resolved.AwsS3 ?? throw new InvalidOperationException("AWS S3 options are not configured.");

        if (string.IsNullOrWhiteSpace(_options.BucketName))
        {
            throw new InvalidOperationException("AWS S3 bucket name is required.");
        }

        if (string.IsNullOrWhiteSpace(_options.Region))
        {
            throw new InvalidOperationException("AWS S3 region is required.");
        }

        if (!string.IsNullOrWhiteSpace(_options.AccessKeyId) && string.IsNullOrWhiteSpace(_options.SecretAccessKey))
        {
            throw new InvalidOperationException("AWS S3 secret access key is required when access key ID is provided.");
        }

        if (!string.IsNullOrWhiteSpace(_options.SecretAccessKey) && string.IsNullOrWhiteSpace(_options.AccessKeyId))
        {
            throw new InvalidOperationException("AWS S3 access key ID is required when secret access key is provided.");
        }

        _client = CreateClient(_options);
        _batchIndex = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
        _uploadCancellationTokens = new ConcurrentDictionary<string, CancellationTokenSource>();
    }

    public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

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
                _options.KeyPrefix);

            var uploadedAt = DateTimeOffset.UtcNow;
            var expiresAt = request.TimeToLive.HasValue
                ? uploadedAt.Add(request.TimeToLive.Value)
                : (DateTimeOffset?)null;

            var metadata = CloudStorageMetadata.BuildMetadata(request.Metadata, request.FileName, expiresAt);

            var putRequest = new PutObjectRequest
            {
                BucketName = _options.BucketName,
                Key = objectKey,
                ContentType = request.ContentType,
                InputStream = request.Content
            };

            if (request.Progress != null)
            {
                var lastProgressUpdate = DateTimeOffset.UtcNow;
                putRequest.StreamTransferProgress += (_, args) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - lastProgressUpdate < TimeSpan.FromMilliseconds(100) && args.TransferredBytes < args.TotalBytes)
                    {
                        return;
                    }

                    var resolvedTotalBytes = args.TotalBytes > 0 ? args.TotalBytes : totalBytes;
                    var progress = new UploadProgress
                    {
                        UploadId = uploadId,
                        Status = OperationStatus.Processing,
                        BytesUploaded = args.TransferredBytes,
                        TotalBytes = resolvedTotalBytes,
                        FileName = request.FileName,
                        ContentType = request.ContentType,
                        StartedAt = initialProgress.StartedAt,
                        CurrentPhase = "Uploading"
                    };

                    _ = _progressStore.SetProgressAsync(uploadId, progress, TimeSpan.FromHours(1), CancellationToken.None);
                    request.Progress.Report(progress);
                    lastProgressUpdate = now;
                };
            }

            foreach (var pair in metadata)
            {
                putRequest.Metadata[pair.Key] = pair.Value;
            }

            if (_options.EnableServerSideEncryption)
            {
                putRequest.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
            }

            await _client.PutObjectAsync(putRequest, linkedCancellationSource.Token);

            var sizeBytes = await ResolveSizeAsync(objectKey, request, cancellationToken);
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
                Provider = CloudStorageProvider.AwsS3
            };

            var completedProgress = new UploadProgress
            {
                UploadId = uploadId,
                Status = OperationStatus.Completed,
                BytesUploaded = sizeBytes,
                TotalBytes = totalBytes > 0 ? totalBytes : sizeBytes,
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
            var response = await _client.GetObjectAsync(_options.BucketName, fileId, cancellationToken);
            return new ResponseDisposingStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
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

        if (!await ExistsAsync(fileId, cancellationToken))
        {
            return false;
        }

        var deleted = await DeleteObjectAsync(fileId, cancellationToken);
        if (deleted)
        {
            FileStorageLog.FileDeleted(_logger, fileId);
        }

        return deleted;
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
                        await DeleteObjectAsync(uploaded.FileId, cancellationToken);
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
                if (await DeleteObjectAsync(fileId, cancellationToken))
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
            var response = await _client.GetObjectMetadataAsync(_options.BucketName, fileId, cancellationToken);
            var metadata = ToMetadataDictionary(response.Metadata);
            var fileName = CloudStorageMetadata.GetFileName(metadata) ?? CloudStoragePath.GetFileNameFromKey(fileId);
            var expiresAt = CloudStorageMetadata.GetExpiresAt(metadata);
            var lastModified = response.LastModified?.ToUniversalTime() ?? DateTime.UtcNow;

            return new CloudFile
            {
                FileId = fileId,
                FileName = fileName,
                StoragePath = fileId,
                ContentType = response.Headers.ContentType ?? "application/octet-stream",
                SizeBytes = response.ContentLength,
                UploadedAt = new DateTimeOffset(lastModified),
                ExpiresAt = expiresAt,
                ContentHash = null,
                Metadata = CloudStorageMetadata.ExtractUserMetadata(metadata),
                Provider = CloudStorageProvider.AwsS3
            };
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        try
        {
            _ = await _client.GetObjectMetadataAsync(_options.BucketName, fileId, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<CloudFile>> ListFilesAsync(
        string? folder = null,
        int maxResults = 1000,
        CancellationToken cancellationToken = default)
    {
        var prefix = CloudStoragePath.BuildPrefix(folder, _options.KeyPrefix);
        var request = new ListObjectsV2Request
        {
            BucketName = _options.BucketName,
            Prefix = prefix,
            MaxKeys = maxResults
        };

        var response = await _client.ListObjectsV2Async(request, cancellationToken);
        var results = new List<CloudFile>();

        foreach (var item in response.S3Objects)
        {
            var metadata = await GetMetadataAsync(item.Key, cancellationToken);
            if (metadata != null)
            {
                results.Add(metadata);
            }

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

        if (!await ExistsAsync(fileId, cancellationToken))
        {
            return null;
        }

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.BucketName,
            Key = fileId,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiresIn ?? _defaultSignedUrlLifetime)
        };

        return _client.GetPreSignedURL(request);
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
            _options.KeyPrefix);

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = DateTime.UtcNow.Add(expiresIn ?? _defaultSignedUrlLifetime)
        };

        var url = _client.GetPreSignedURL(request);
        return Task.FromResult<(string Url, string FileId)?>((url, objectKey));
    }

    public async Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var prefix = CloudStoragePath.BuildPrefix(null, _options.KeyPrefix);
        var request = new ListObjectsV2Request
        {
            BucketName = _options.BucketName,
            Prefix = prefix
        };

        var cleanedCount = 0;
        string? continuationToken = null;

        do
        {
            request.ContinuationToken = continuationToken;
            var response = await _client.ListObjectsV2Async(request, cancellationToken);

            foreach (var item in response.S3Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = await GetMetadataDictionaryAsync(item.Key, cancellationToken);
                var expiresAt = CloudStorageMetadata.GetExpiresAt(metadata);
                if (expiresAt.HasValue && expiresAt.Value <= now)
                {
                    if (await DeleteObjectAsync(item.Key, cancellationToken))
                    {
                        cleanedCount++;
                    }
                }
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (!string.IsNullOrEmpty(continuationToken));

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

    private static AmazonS3Client CreateClient(AwsS3Options options)
    {
        var config = new AmazonS3Config
        {
            RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region),
            ForcePathStyle = options.ForcePathStyle
        };

        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
        }

        if (!string.IsNullOrWhiteSpace(options.AccessKeyId) && !string.IsNullOrWhiteSpace(options.SecretAccessKey))
        {
            var credentials = new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey);
            return new AmazonS3Client(credentials, config);
        }

        return new AmazonS3Client(config);
    }

    private static bool IsNotFound(AmazonS3Exception exception)
    {
        return exception.StatusCode == HttpStatusCode.NotFound
               || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ToMetadataDictionary(MetadataCollection metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in metadata.Keys)
        {
            if (key is null)
            {
                continue;
            }

            var value = metadata[key];
            if (!string.IsNullOrEmpty(value))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private async Task<long> ResolveSizeAsync(string objectKey, FileUploadRequest request, CancellationToken cancellationToken)
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
            var response = await _client.GetObjectMetadataAsync(_options.BucketName, objectKey, cancellationToken);
            return response.ContentLength;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return 0;
        }
    }

    private async Task<Dictionary<string, string>?> GetMetadataDictionaryAsync(string fileId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.GetObjectMetadataAsync(_options.BucketName, fileId, cancellationToken);
            return ToMetadataDictionary(response.Metadata);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    private async Task<bool> DeleteObjectAsync(string fileId, CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteObjectAsync(_options.BucketName, fileId, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return false;
        }
        catch (Exception ex)
        {
            FileStorageLog.FileDeleteFailed(_logger, ex, fileId);
            return false;
        }
    }

    private async Task<int> DeleteByPrefixAsync(string batchId, CancellationToken cancellationToken)
    {
        var prefix = CloudStoragePath.BuildPrefix(batchId, _options.KeyPrefix);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return 0;
        }

        var request = new ListObjectsV2Request
        {
            BucketName = _options.BucketName,
            Prefix = prefix
        };

        var deletedCount = 0;
        string? continuationToken = null;

        do
        {
            request.ContinuationToken = continuationToken;
            var response = await _client.ListObjectsV2Async(request, cancellationToken);
            foreach (var item in response.S3Objects)
            {
                if (await DeleteObjectAsync(item.Key, cancellationToken))
                {
                    deletedCount++;
                }
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (!string.IsNullOrEmpty(continuationToken));

        if (deletedCount > 0)
        {
            FileStorageLog.BatchDeleted(_logger, batchId, deletedCount);
        }

        return deletedCount;
    }
}

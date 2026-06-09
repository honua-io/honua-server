// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

internal sealed class AwsS3FileStorage : CloudFileStorageBase
{
    private readonly TimeSpan _signedUrlLifetime;

    private readonly AwsS3Options _options;
    private readonly AmazonS3Client _client;

    public AwsS3FileStorage(
        IOptions<CloudStorageOptions> options,
        ILogger<AwsS3FileStorage> logger,
        IUploadProgressStore progressStore)
        : base(logger, progressStore)
    {
        ArgumentNullException.ThrowIfNull(options);

        var resolved = options.Value ?? throw new ArgumentNullException(nameof(options));
        _signedUrlLifetime = resolved.SignedUrlLifetime;
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
    }

    public override CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

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
        ReportProgressToCaller(request.Progress, uploadId, initialProgress);
        SerializedUploadProgressWriter? progressWriter = null;

        try
        {
            var objectKey = !string.IsNullOrWhiteSpace(request.ObjectKeyOverride)
                ? request.ObjectKeyOverride
                : CloudStoragePath.BuildObjectKey(
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
                var writer = CreateProgressWriter(uploadId);
                progressWriter = writer;
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

                    writer.Report(progress);
                    ReportProgressToCaller(request.Progress, uploadId, progress);
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
            if (progressWriter != null)
            {
                await progressWriter.CloseAndDrainAsync().ConfigureAwait(false);
            }

            await ProgressStore.SetProgressAsync(uploadId, completedProgress, TimeSpan.FromHours(1), cancellationToken);
            ReportProgressToCaller(request.Progress, uploadId, completedProgress);

            stopwatch.Stop();
            FileStorageLog.FileUploaded(Logger, request.FileName, sizeBytes, objectKey, stopwatch.ElapsedMilliseconds);

            return UploadResult.CreateSuccess(cloudFile, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (linkedCancellationSource.Token.IsCancellationRequested)
        {
            stopwatch.Stop();
            if (progressWriter != null)
            {
                await progressWriter.CloseAndDrainAsync().ConfigureAwait(false);
            }

            await ReportCancelledUploadAsync(uploadId, request, totalBytes, initialProgress.StartedAt);
            throw;
        }
        catch (ArgumentException ex)
        {
            var (progressMessage, resultMessage) = ResolveArgumentFailureMessages(ex);
            if (progressWriter != null)
            {
                await progressWriter.CloseAndDrainAsync().ConfigureAwait(false);
            }

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
            if (progressWriter != null)
            {
                await progressWriter.CloseAndDrainAsync().ConfigureAwait(false);
            }

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
            var response = await _client.GetObjectAsync(_options.BucketName, fileId, cancellationToken);
            return new ResponseDisposingStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }


    public override async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        if (!await ExistsAsync(fileId, cancellationToken))
        {
            return false;
        }

        var deleted = await DeleteObjectAsync(fileId, cancellationToken);
        if (deleted)
        {
            FileStorageLog.FileDeleted(Logger, fileId);
        }

        return deleted;
    }

    public override async Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
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

    public override async Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
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

    public override async Task<IReadOnlyList<CloudFile>> ListFilesAsync(
        string? folder = null,
        int maxResults = 1000,
        CancellationToken cancellationToken = default)
    {
        var prefix = CloudStoragePath.BuildPrefix(folder, _options.KeyPrefix);
        var results = new List<CloudFile>();
        string? continuationToken = null;

        do
        {
            var remaining = maxResults == int.MaxValue
                ? 1000
                : Math.Max(1, Math.Min(1000, maxResults - results.Count));
            var request = new ListObjectsV2Request
            {
                BucketName = _options.BucketName,
                Prefix = prefix,
                MaxKeys = remaining,
                ContinuationToken = continuationToken
            };

            var response = await _client.ListObjectsV2Async(request, cancellationToken);
            foreach (var item in response.S3Objects)
            {
                var metadata = await GetMetadataAsync(item.Key, cancellationToken);
                if (metadata != null)
                {
                    results.Add(metadata);
                }

                if (results.Count >= maxResults)
                {
                    return results;
                }
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));

        return results;
    }

    public override async Task<string?> GetPresignedUrlAsync(
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
            Expires = DateTime.UtcNow.Add(expiresIn ?? _signedUrlLifetime)
        };

        return _client.GetPreSignedURL(request);
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
            _options.KeyPrefix);

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = DateTime.UtcNow.Add(expiresIn ?? _signedUrlLifetime)
        };

        var url = _client.GetPreSignedURL(request);
        return Task.FromResult<(string Url, string FileId)?>((url, objectKey));
    }

    public override async Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
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
            FileStorageLog.ExpiredFilesCleaned(Logger, cleanedCount);
        }

        return cleanedCount;
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
        const string metadataPrefix = "x-amz-meta-";
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
                var normalizedKey = key.StartsWith(metadataPrefix, StringComparison.OrdinalIgnoreCase)
                    ? key[metadataPrefix.Length..]
                    : key;
                result[normalizedKey] = value;
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
            FileStorageLog.FileDeleteFailed(Logger, ex, fileId);
            return false;
        }
    }

    protected override async Task<int> DeleteByPrefixAsync(string batchId, CancellationToken cancellationToken)
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
            FileStorageLog.BatchDeleted(Logger, batchId, deletedCount);
        }

        return deletedCount;
    }

    protected override Task<bool> DeleteBatchFileAsync(string fileId, CancellationToken cancellationToken)
        => DeleteObjectAsync(fileId, cancellationToken);
}

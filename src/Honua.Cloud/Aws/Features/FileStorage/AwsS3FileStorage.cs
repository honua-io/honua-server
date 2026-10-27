// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Honua.Cloud.Aws.Features;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Internal;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

internal sealed class AwsS3FileStorage : CloudFileStorageBase
{
    /// <summary>S3 rejects one PutObject larger than 5 GiB. Multipart is required above that.</summary>
    internal const long S3SinglePutMaxBytes = 5L * 1024 * 1024 * 1024;

    private const int DefaultPartBytes = 16 * 1024 * 1024;
    private const int MaxPartCount = 10_000;
    private const int MaxPartBytes = 128 * 1024 * 1024;

    private readonly TimeSpan _signedUrlLifetime;

    private readonly AwsS3Options _options;
    private readonly IAmazonS3 _client;

    public AwsS3FileStorage(
        IOptions<CloudStorageOptions> options,
        ILogger<AwsS3FileStorage> logger,
        IUploadProgressStore progressStore)
        : this(options, logger, progressStore, client: null)
    {
    }

    /// <summary>
    /// Test seam. Production construction passes <see langword="null"/> and builds the SDK client.
    /// </summary>
    internal AwsS3FileStorage(
        IOptions<CloudStorageOptions> options,
        ILogger<AwsS3FileStorage> logger,
        IUploadProgressStore progressStore,
        IAmazonS3? client)
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

        _client = client ?? CreateClient(_options);
    }

    public override CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

    public override Task<UploadResult> UploadAsync(
        FileUploadRequest request,
        CancellationToken cancellationToken = default)
        => UploadCoreAsync(request, conditional: false, expectedETag: null, cancellationToken);

    public override Task<UploadResult> UploadIfMatchAsync(
        FileUploadRequest request,
        string? expectedETag,
        CancellationToken cancellationToken = default)
        => UploadCoreAsync(request, conditional: true, expectedETag, cancellationToken);

    private async Task<UploadResult> UploadCoreAsync(
        FileUploadRequest request,
        bool conditional,
        string? expectedETag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (conditional && expectedETag is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag);
        }

        var stopwatch = Stopwatch.StartNew();
        var uploadId = request.UploadId;
        var linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var totalBytes = request.SizeBytes ?? (request.Content.CanSeek ? request.Content.Length : 0);

        UploadCancellationTokens[uploadId] = linkedCancellationSource;

        var initialProgress = UploadProgress.CreateInitial(uploadId, request.FileName, totalBytes, request.ContentType);
        await TrySetUploadProgressAsync(uploadId, initialProgress, TimeSpan.FromHours(1), cancellationToken);
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
            var knownLength = KnownContentLength(request);
            string? etag;
            if (RequiresMultipartUpload(conditional, knownLength))
            {
                if (request.Progress != null)
                {
                    progressWriter = CreateProgressWriter(uploadId);
                }

                etag = await UploadMultipartAsync(
                    request,
                    objectKey,
                    metadata,
                    knownLength,
                    uploadId,
                    initialProgress,
                    progressWriter,
                    linkedCancellationSource.Token).ConfigureAwait(false);
            }
            else
            {
                var putRequest = new PutObjectRequest
                {
                    BucketName = _options.BucketName,
                    Key = objectKey,
                    ContentType = request.ContentType,
                    InputStream = request.Content
                };
                if (conditional)
                {
                    if (expectedETag is null)
                    {
                        putRequest.IfNoneMatch = "*";
                    }
                    else
                    {
                        putRequest.IfMatch = expectedETag;
                    }
                }

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

                var putResponse = await _client.PutObjectAsync(putRequest, linkedCancellationSource.Token);
                etag = putResponse.ETag;
            }

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
                ETag = etag,
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

            await TrySetUploadProgressAsync(uploadId, completedProgress, TimeSpan.FromHours(1), cancellationToken);
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
        // Intentionally generic: this boundary must convert any failure from the AWS
        // SDK, the local progress writer, or an unexpected bug into a reported failed
        // upload rather than letting it propagate; the exception is logged via
        // ReportFailedUploadAsync -> FileStorageLog.FileUploadFailed.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
            // Only dispose the linked CTS when this code path successfully removes it
            // from the shared dictionary (transfer of ownership). If the cancel path
            // already removed it and is between TryRemove and CancelAsync, disposing
            // here would cause ObjectDisposedException on the cancel path's CancelAsync
            // call. The cancel path is responsible for disposing when it removes the CTS.
            // Not a `using` statement: the CTS is not owned/created in this scope, it is
            // conditionally reclaimed from a shared dictionary via TryRemove.
            if (UploadCancellationTokens.TryRemove(uploadId, out var removedSource))
            {
                DeferredDisposal.Dispose(removedSource);
            }
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

    public override async Task<bool> DeleteIfMatchAsync(
        string fileId,
        string expectedETag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag);

        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _options.BucketName,
                Key = fileId,
                IfMatch = expectedETag
            }, cancellationToken);
            FileStorageLog.FileDeleted(Logger, fileId);
            return true;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex) || ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
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
                ETag = response.ETag,
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
        bool includeMetadata = true,
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

            // AWS SDK v4 leaves response collections null when S3 returns no items (an empty
            // prefix produces a ListBucketResult with no <Contents>, unmarshalled as
            // S3Objects == null rather than an empty list). Normalize before dereferencing —
            // an unguarded .Count here NRE'd the temporary-file quota listing against the
            // empty temporary-files/ prefix and 500'd every rendered map-image href/f=json
            // export on S3-backed deployments (honua-server#2311).
            var s3Objects = response.S3Objects ?? [];

            // ListObjectsV2 already carries the key, size, and last-modified for every object, so a
            // size/last-modified-only listing needs no per-object HEAD. Only fetch per-object
            // metadata (HeadObject) when the caller explicitly asks for the custom user metadata
            // (original file name, expiry, custom keys) that the flat listing cannot return — and
            // even then bound the parallelism so a page is N concurrent HEADs rather than N
            // sequential round trips.
            var pageBatch = new CloudFile?[s3Objects.Count];
            if (includeMetadata)
            {
                await Parallel.ForEachAsync(
                    s3Objects.Select((item, index) => (item, index)),
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                    async (entry, ct) =>
                    {
                        var metadata = await GetMetadataAsync(entry.item.Key, ct).ConfigureAwait(false);
                        if (metadata != null)
                        {
                            pageBatch[entry.index] = metadata;
                        }
                    });
            }
            else
            {
                for (var index = 0; index < s3Objects.Count; index++)
                {
                    pageBatch[index] = ToCloudFile(s3Objects[index]);
                }
            }

            foreach (var cloudFile in pageBatch)
            {
                if (cloudFile is null)
                {
                    continue;
                }

                results.Add(cloudFile);
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

    private static CloudFile ToCloudFile(S3Object item)
    {
        // Built from the flat ListObjectsV2 entry alone (no HEAD). S3's list API does not return
        // content type or user metadata, so FileName falls back to the key and ContentType to the
        // generic octet-stream; callers that need the original file name, expiry, or custom
        // metadata must request includeMetadata: true (or call GetMetadataAsync per file).
        var lastModified = item.LastModified?.ToUniversalTime() ?? DateTime.UtcNow;
        return new CloudFile
        {
            FileId = item.Key,
            FileName = CloudStoragePath.GetFileNameFromKey(item.Key),
            StoragePath = item.Key,
            ContentType = "application/octet-stream",
            SizeBytes = item.Size ?? 0,
            UploadedAt = new DateTimeOffset(lastModified),
            ExpiresAt = null,
            ContentHash = null,
            Provider = CloudStorageProvider.AwsS3
        };
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

        if (_options.EnableServerSideEncryption)
        {
            // Bake the SSE requirement into the signed URL so a direct upload through it must send
            // the x-amz-server-side-encryption header, matching the server-side PutObjectAsync path.
            // Without this, direct uploads via the presigned URL would land unencrypted even when
            // server-side encryption is configured.
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        }

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

            // SDK v4: S3Objects is null (not empty) when the listing has no results.
            foreach (var item in response.S3Objects ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = await GetMetadataDictionaryAsync(item.Key, cancellationToken);
                var expiresAt = CloudStorageMetadata.GetExpiresAt(metadata);
                if (expiresAt.HasValue && expiresAt.Value <= now && await DeleteObjectAsync(item.Key, cancellationToken))
                {
                    cleanedCount++;
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

    // Shared with the COG / Zarr range reader so both S3 clients resolve region and endpoint alike.
    internal static AmazonS3Client CreateClient(AwsS3Options options)
    {
        var config = new AmazonS3Config { ForcePathStyle = options.ForcePathStyle };
        AwsClientEndpoint.Apply(config, options.Region, options.ServiceUrl);

        if (!string.IsNullOrWhiteSpace(options.AccessKeyId) && !string.IsNullOrWhiteSpace(options.SecretAccessKey))
        {
            var credentials = new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey);
            return new AmazonS3Client(credentials, config);
        }

        return new AmazonS3Client(config);
    }

    /// <summary>
    /// A single PUT is unsafe when the object is larger than 5 GiB or its length is not known.
    /// Conditional cache writes stay on PutObject; those objects are small.
    /// </summary>
    internal static bool RequiresMultipartUpload(bool conditional, long? knownLength)
        => !conditional && knownLength is null or > S3SinglePutMaxBytes;

    /// <summary>
    /// Part size that stays under S3's 10,000-part cap for <paramref name="knownLength"/>.
    /// Unknown lengths use <see cref="DefaultPartBytes"/>, which covers the 50 GiB import default.
    /// </summary>
    internal static int MultipartPartSize(long? knownLength)
    {
        long part = DefaultPartBytes;
        if (knownLength is > S3SinglePutMaxBytes)
        {
            var minimum = (knownLength.Value + MaxPartCount - 1) / MaxPartCount;
            if (minimum > part)
            {
                part = minimum;
            }
        }

        const long mebibyte = 1024 * 1024;
        part = (part + mebibyte - 1) / mebibyte * mebibyte;
        if (part is < DefaultPartBytes or > MaxPartBytes)
        {
            throw new InvalidOperationException("The object is too large to stage with one multipart upload.");
        }

        return (int)part;
    }

    private static long? KnownContentLength(FileUploadRequest request)
    {
        if (request.SizeBytes.HasValue)
        {
            return request.SizeBytes.Value;
        }

        if (request.Content.CanSeek)
        {
            return request.Content.Length;
        }

        return null;
    }

    private async Task<string?> UploadMultipartAsync(
        FileUploadRequest request,
        string objectKey,
        Dictionary<string, string> metadata,
        long? knownLength,
        string uploadId,
        UploadProgress initialProgress,
        SerializedUploadProgressWriter? progressWriter,
        CancellationToken cancellationToken)
    {
        var partSize = MultipartPartSize(knownLength);
        var buffer = new byte[partSize];
        var filled = await ReadPartAsync(request.Content, buffer, cancellationToken).ConfigureAwait(false);
        if (filled == 0)
        {
            // Nothing to split. An empty object is within the single-PUT limit.
            return await PutEmptyObjectAsync(request, objectKey, metadata, cancellationToken).ConfigureAwait(false);
        }

        string? openUploadId = null;
        try
        {
            var initiate = new InitiateMultipartUploadRequest
            {
                BucketName = _options.BucketName,
                Key = objectKey,
                ContentType = request.ContentType,
            };
            foreach (var pair in metadata)
            {
                initiate.Metadata[pair.Key] = pair.Value;
            }

            if (_options.EnableServerSideEncryption)
            {
                initiate.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
            }

            var initiated = await _client.InitiateMultipartUploadAsync(initiate, cancellationToken).ConfigureAwait(false);
            openUploadId = initiated.UploadId;
            var parts = new List<PartETag>(capacity: 16);
            long sent = 0;
            var partNumber = 1;
            while (filled > 0)
            {
                if (partNumber > MaxPartCount)
                {
                    throw new InvalidOperationException("The object is too large to stage with one multipart upload.");
                }

                using var partStream = new MemoryStream(buffer, 0, filled, writable: false, publiclyVisible: true);
                var uploaded = await _client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _options.BucketName,
                    Key = objectKey,
                    UploadId = openUploadId,
                    PartNumber = partNumber,
                    PartSize = filled,
                    InputStream = partStream,
                }, cancellationToken).ConfigureAwait(false);
                parts.Add(new PartETag(partNumber, uploaded.ETag));
                sent += filled;
                ReportMultipartProgress(request, uploadId, initialProgress, progressWriter, sent, knownLength);
                partNumber++;
                filled = await ReadPartAsync(request.Content, buffer, cancellationToken).ConfigureAwait(false);
            }

            var completed = await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _options.BucketName,
                Key = objectKey,
                UploadId = openUploadId,
                PartETags = parts,
            }, cancellationToken).ConfigureAwait(false);
            openUploadId = null;
            return completed.ETag;
        }
        catch
        {
            if (openUploadId is not null)
            {
                try
                {
                    await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                    {
                        BucketName = _options.BucketName,
                        Key = objectKey,
                        UploadId = openUploadId,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception abortEx) when (abortEx is not OutOfMemoryException)
                {
                    // The original upload failure is what the caller observes.
                    FileStorageLog.FileDeleteFailed(Logger, abortEx, objectKey);
                }
            }

            throw;
        }
    }

    private async Task<string?> PutEmptyObjectAsync(
        FileUploadRequest request,
        string objectKey,
        Dictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        using var empty = new MemoryStream([], writable: false);
        var putRequest = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            ContentType = request.ContentType,
            InputStream = empty,
        };
        foreach (var pair in metadata)
        {
            putRequest.Metadata[pair.Key] = pair.Value;
        }

        if (_options.EnableServerSideEncryption)
        {
            putRequest.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        }

        var response = await _client.PutObjectAsync(putRequest, cancellationToken).ConfigureAwait(false);
        return response.ETag;
    }

    private void ReportMultipartProgress(
        FileUploadRequest request,
        string uploadId,
        UploadProgress initialProgress,
        SerializedUploadProgressWriter? progressWriter,
        long sent,
        long? knownLength)
    {
        if (progressWriter is null && request.Progress is null)
        {
            return;
        }

        var progress = new UploadProgress
        {
            UploadId = uploadId,
            Status = OperationStatus.Processing,
            BytesUploaded = sent,
            TotalBytes = knownLength is > 0 ? knownLength.Value : sent,
            FileName = request.FileName,
            ContentType = request.ContentType,
            StartedAt = initialProgress.StartedAt,
            CurrentPhase = "Uploading",
        };
        progressWriter?.Report(progress);
        ReportProgressToCaller(request.Progress, uploadId, progress);
    }

    private static async Task<int> ReadPartAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return filled;
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
        // Intentionally generic: covers transport/auth/throttling failures beyond
        // AmazonS3Exception so a single delete failure never aborts batch cleanup;
        // the exception is logged via FileStorageLog.FileDeleteFailed.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

            // SDK v4: S3Objects is null (not empty) when the listing has no results.
            // Not rewritten with .Where(): the filter predicate (DeleteObjectAsync) is
            // async, which plain LINQ .Where() cannot express without buffering tasks.
            foreach (var item in response.S3Objects ?? [])
            {
                deletedCount += await DeleteObjectAsync(item.Key, cancellationToken).ConfigureAwait(false) ? 1 : 0;
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

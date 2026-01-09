// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace Honua.Server.Features.FileStorage;

internal sealed class GoogleCloudStorageFileStorage : ICloudFileStorage
{
    private static readonly TimeSpan _defaultSignedUrlLifetime = TimeSpan.FromMinutes(15);

    private readonly GoogleCloudStorageOptions _options;
    private readonly StorageClient _client;
    private readonly UrlSigner? _urlSigner;
    private readonly ILogger<GoogleCloudStorageFileStorage> _logger;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _batchIndex;

    public GoogleCloudStorageFileStorage(IOptions<CloudStorageOptions> options, ILogger<GoogleCloudStorageFileStorage> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var resolved = options.Value ?? throw new ArgumentNullException(nameof(options));
        _options = resolved.GoogleCloudStorage ?? throw new InvalidOperationException("Google Cloud Storage options are not configured.");

        if (string.IsNullOrWhiteSpace(_options.BucketName))
        {
            throw new InvalidOperationException("Google Cloud Storage bucket name is required.");
        }

        if (string.IsNullOrWhiteSpace(_options.ProjectId))
        {
            throw new InvalidOperationException("Google Cloud Storage project ID is required.");
        }

        var credential = CreateCredential(_options.CredentialsPath);
        _client = StorageClient.Create(credential);
        _urlSigner = TryCreateUrlSigner();
        _batchIndex = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
    }

    public CloudStorageProvider Provider => CloudStorageProvider.GoogleCloudStorage;

    public async Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var objectKey = CloudStoragePath.BuildObjectKey(
                CloudStoragePath.GenerateFileId(),
                request.FileName,
                request.Folder,
                _options.ObjectPrefix);

            var uploadedAt = DateTimeOffset.UtcNow;
            var expiresAt = request.TimeToLive.HasValue
                ? uploadedAt.Add(request.TimeToLive.Value)
                : (DateTimeOffset?)null;

            var metadata = CloudStorageMetadata.BuildMetadata(request.Metadata, request.FileName, expiresAt);

            var storageObject = new StorageObject
            {
                Bucket = _options.BucketName,
                Name = objectKey,
                ContentType = request.ContentType,
                Metadata = metadata
            };

            var uploaded = await _client.UploadObjectAsync(storageObject, request.Content, null, cancellationToken, null);
            var sizeBytes = ResolveSize(uploaded, request);

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
                Provider = CloudStorageProvider.GoogleCloudStorage
            };

            stopwatch.Stop();
            FileStorageLog.FileUploaded(_logger, request.FileName, sizeBytes, objectKey, stopwatch.ElapsedMilliseconds);

            return UploadResult.CreateSuccess(cloudFile, stopwatch.Elapsed);
        }
        catch (ArgumentException ex)
        {
            stopwatch.Stop();
            FileStorageLog.FileUploadFailed(_logger, ex, request.FileName);
            return UploadResult.CreateFailure(ex.Message, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            FileStorageLog.FileUploadFailed(_logger, ex, request.FileName);
            return UploadResult.CreateFailure("File upload failed.", stopwatch.Elapsed);
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

        if (!await ExistsAsync(fileId, cancellationToken))
        {
            return null;
        }

        return CreateDownloadStream(fileId, cancellationToken);
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
            await _client.DeleteObjectAsync(_options.BucketName, fileId, null, cancellationToken);
            FileStorageLog.FileDeleted(_logger, fileId);
            return true;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception ex)
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
            var storageObject = await _client.GetObjectAsync(_options.BucketName, fileId, null, cancellationToken);
            var metadata = storageObject.Metadata;
            var fileName = CloudStorageMetadata.GetFileName(metadata) ?? CloudStoragePath.GetFileNameFromKey(fileId);
            var expiresAt = CloudStorageMetadata.GetExpiresAt(metadata);
            var sizeBytes = ResolveSize(storageObject, null);
            var uploadedAt = storageObject.UpdatedDateTimeOffset
                             ?? storageObject.TimeCreatedDateTimeOffset
                             ?? DateTimeOffset.UtcNow;

            return new CloudFile
            {
                FileId = fileId,
                FileName = fileName,
                StoragePath = fileId,
                ContentType = storageObject.ContentType ?? "application/octet-stream",
                SizeBytes = sizeBytes,
                UploadedAt = uploadedAt,
                ExpiresAt = expiresAt,
                ContentHash = null,
                Metadata = CloudStorageMetadata.ExtractUserMetadata(metadata),
                Provider = CloudStorageProvider.GoogleCloudStorage
            };
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        try
        {
            _ = await _client.GetObjectAsync(_options.BucketName, fileId, null, cancellationToken);
            return true;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<CloudFile>> ListFilesAsync(
        string? folder = null,
        int maxResults = 1000,
        CancellationToken cancellationToken = default)
    {
        var prefix = CloudStoragePath.BuildPrefix(folder, _options.ObjectPrefix);
        var options = new ListObjectsOptions
        {
            Projection = Projection.Full,
            PageSize = maxResults
        };

        var results = new List<CloudFile>();
        await foreach (var storageObject in _client.ListObjectsAsync(_options.BucketName, prefix ?? string.Empty, options)
                           .WithCancellation(cancellationToken))
        {
            var metadata = storageObject.Metadata;
            var fileName = CloudStorageMetadata.GetFileName(metadata) ?? CloudStoragePath.GetFileNameFromKey(storageObject.Name);
            var expiresAt = CloudStorageMetadata.GetExpiresAt(metadata);
            var sizeBytes = ResolveSize(storageObject, null);
            var uploadedAt = storageObject.UpdatedDateTimeOffset
                             ?? storageObject.TimeCreatedDateTimeOffset
                             ?? DateTimeOffset.UtcNow;

            results.Add(new CloudFile
            {
                FileId = storageObject.Name,
                FileName = fileName,
                StoragePath = storageObject.Name,
                ContentType = storageObject.ContentType ?? "application/octet-stream",
                SizeBytes = sizeBytes,
                UploadedAt = uploadedAt,
                ExpiresAt = expiresAt,
                ContentHash = null,
                Metadata = CloudStorageMetadata.ExtractUserMetadata(metadata),
                Provider = CloudStorageProvider.GoogleCloudStorage
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

        if (_urlSigner == null)
        {
            return null;
        }

        if (!await ExistsAsync(fileId, cancellationToken))
        {
            return null;
        }

        return await _urlSigner.SignAsync(
            _options.BucketName,
            fileId,
            expiresIn ?? _defaultSignedUrlLifetime,
            HttpMethod.Get,
            null,
            cancellationToken);
    }

    public async Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
        string fileName,
        string contentType,
        TimeSpan? expiresIn = null,
        string? folder = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        if (_urlSigner == null)
        {
            return null;
        }

        var objectKey = CloudStoragePath.BuildObjectKey(
            CloudStoragePath.GenerateFileId(),
            fileName,
            folder,
            _options.ObjectPrefix);

        var template = UrlSigner.RequestTemplate
            .FromBucket(_options.BucketName)
            .WithObjectName(objectKey)
            .WithHttpMethod(HttpMethod.Put)
            .WithContentHeaders(new[]
            {
                new KeyValuePair<string, IEnumerable<string>>("Content-Type", new[] { contentType })
            });

        var signerOptions = UrlSigner.Options.FromDuration(expiresIn ?? _defaultSignedUrlLifetime);
        var url = await _urlSigner.SignAsync(template, signerOptions, cancellationToken);
        return (url, objectKey);
    }

    public async Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var prefix = CloudStoragePath.BuildPrefix(null, _options.ObjectPrefix);
        var options = new ListObjectsOptions
        {
            Projection = Projection.Full
        };

        var cleanedCount = 0;
        await foreach (var storageObject in _client.ListObjectsAsync(_options.BucketName, prefix ?? string.Empty, options)
                           .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expiresAt = CloudStorageMetadata.GetExpiresAt(storageObject.Metadata);
            if (expiresAt.HasValue && expiresAt.Value <= now)
            {
                try
                {
                    await _client.DeleteObjectAsync(_options.BucketName, storageObject.Name, null, cancellationToken);
                    cleanedCount++;
                }
                catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
                {
                    // Ignore missing objects
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
        // TODO: Implement progress tracking for Google Cloud Storage uploads
        // For now, return null indicating no progress tracking support
        return Task.FromResult<UploadProgress?>(null);
    }

    /// <inheritdoc />
    public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        // TODO: Implement upload cancellation for Google Cloud Storage uploads
        // For now, return false indicating cancellation not supported
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
    {
        // TODO: Implement active uploads tracking for Google Cloud Storage
        // For now, return empty list
        return Task.FromResult<IReadOnlyList<UploadProgress>>(Array.Empty<UploadProgress>());
    }

    private static GoogleCredential CreateCredential(string? credentialsPath)
    {
        return string.IsNullOrWhiteSpace(credentialsPath)
            ? GoogleCredential.GetApplicationDefault()
            : GoogleCredential.FromFile(credentialsPath);
    }

    private UrlSigner? TryCreateUrlSigner()
    {
        try
        {
            return _client.CreateUrlSigner();
        }
        catch (InvalidOperationException)
        {
            _logger.LogDebug("Google Cloud Storage credentials do not support URL signing.");
            return null;
        }
    }

    private CancellableReadStream CreateDownloadStream(string objectName, CancellationToken cancellationToken)
    {
        var pipe = new Pipe();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var writerStream = pipe.Writer.AsStream();

        var downloadTask = Task.Run(async () =>
        {
            try
            {
                await _client.DownloadObjectAsync(
                    _options.BucketName,
                    objectName,
                    writerStream,
                    null,
                    linkedCts.Token,
                    null);
                await pipe.Writer.CompleteAsync();
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                await pipe.Writer.CompleteAsync();
            }
            catch (Exception ex)
            {
                await pipe.Writer.CompleteAsync(ex);
            }
            finally
            {
                await writerStream.DisposeAsync();
            }
        }, CancellationToken.None);

        return new CancellableReadStream(pipe.Reader.AsStream(), linkedCts, downloadTask);
    }

    private static long ResolveSize(StorageObject? storageObject, FileUploadRequest? request)
    {
        if (request?.SizeBytes.HasValue == true)
        {
            return request.SizeBytes.Value;
        }

        if (request?.Content.CanSeek == true)
        {
            return request.Content.Length;
        }

        if (storageObject?.Size == null)
        {
            return 0;
        }

        var size = storageObject.Size ?? 0;
        return Convert.ToInt64(size);
    }

    private async Task<int> DeleteByPrefixAsync(string batchId, CancellationToken cancellationToken)
    {
        var prefix = CloudStoragePath.BuildPrefix(batchId, _options.ObjectPrefix);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return 0;
        }

        var deletedCount = 0;
        await foreach (var storageObject in _client.ListObjectsAsync(_options.BucketName, prefix, null).WithCancellation(cancellationToken))
        {
            try
            {
                await _client.DeleteObjectAsync(_options.BucketName, storageObject.Name, null, cancellationToken);
                deletedCount++;
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                // Ignore missing objects
            }
        }

        if (deletedCount > 0)
        {
            FileStorageLog.BatchDeleted(_logger, batchId, deletedCount);
        }

        return deletedCount;
    }
}

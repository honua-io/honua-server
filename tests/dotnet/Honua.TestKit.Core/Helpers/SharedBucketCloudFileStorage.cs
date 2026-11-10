// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.TestKit.Helpers;

// One bucket that several storage clients share, as several hosts share one S3 bucket. Objects are
// keyed by their full key; a listing folder is resolved under the client's key prefix, and a
// download is forward-only, as an S3 or Azure Blob response body is.
public sealed class SharedBucket
{
    internal ConcurrentDictionary<string, StoredObject> Objects { get; } = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Keys => Objects.Keys.ToArray();

    public string? ContentTypeOf(string key) => Objects.TryGetValue(key, out var stored) ? stored.ContentType : null;

    internal sealed record StoredObject(byte[] Bytes, string ContentType, DateTimeOffset UploadedAt);
}

public sealed class SharedBucketCloudFileStorage(
    SharedBucket bucket,
    CloudStorageProvider provider = CloudStorageProvider.AwsS3,
    string keyPrefix = "") : ICloudFileStorage
{
    public CloudStorageProvider Provider => provider;

    public Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
        => StoreAsync(request, mustBeAbsent: false, cancellationToken);

    public Task<UploadResult> UploadIfMatchAsync(
        FileUploadRequest request,
        string? expectedETag,
        CancellationToken cancellationToken = default)
        => expectedETag is null
            ? StoreAsync(request, mustBeAbsent: true, cancellationToken)
            : throw new NotSupportedException("Only create-if-absent is modelled.");

    public Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
        => Task.FromResult<Stream?>(bucket.Objects.TryGetValue(fileId, out var stored) ? new ForwardOnlyStream(stored.Bytes) : null);

    public Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default)
        => Task.FromResult(bucket.Objects.TryGetValue(fileId, out var stored) ? stored.Bytes : null);

    public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
        => Task.FromResult(bucket.Objects.TryRemove(fileId, out _));

    public Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
        => Task.FromResult(bucket.Objects.TryGetValue(fileId, out var stored) ? ToCloudFile(fileId, stored) : null);

    public Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
        => Task.FromResult(bucket.Objects.ContainsKey(fileId));

    public Task<IReadOnlyList<CloudFile>> ListFilesAsync(
        string? folder = null,
        int maxResults = 1000,
        bool includeMetadata = true,
        CancellationToken cancellationToken = default)
    {
        var listedPrefix = string.Join('/', new[] { keyPrefix, folder }.Where(part => !string.IsNullOrEmpty(part))) + "/";
        IReadOnlyList<CloudFile> files = bucket.Objects
            .Where(entry => entry.Key.StartsWith(listedPrefix, StringComparison.Ordinal))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(entry => ToCloudFile(entry.Key, entry.Value))
            .ToList();
        return Task.FromResult(files);
    }

    public Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<string?> GetPresignedUrlAsync(
        string fileId,
        TimeSpan? expiresIn = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Staged outputs are never handed out as presigned URLs.");

    public Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
        string fileName,
        string contentType,
        TimeSpan? expiresIn = null,
        string? folder = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    private async Task<UploadResult> StoreAsync(FileUploadRequest request, bool mustBeAbsent, CancellationToken cancellationToken)
    {
        var key = request.ObjectKeyOverride
            ?? throw new InvalidOperationException("The shared bucket double stores only explicitly keyed objects.");
        if (mustBeAbsent && !request.Content.CanSeek)
        {
            return UploadResult.CreateFailure("A conditional create is one request and needs a seekable body, as on S3.");
        }

        using var buffer = new MemoryStream();
        await request.Content.CopyToAsync(buffer, cancellationToken);
        var stored = new SharedBucket.StoredObject(buffer.ToArray(), request.ContentType, DateTimeOffset.UtcNow);
        if (mustBeAbsent)
        {
            if (!bucket.Objects.TryAdd(key, stored))
            {
                return UploadResult.CreateFailure("The object already exists.");
            }
        }
        else
        {
            bucket.Objects[key] = stored;
        }

        return UploadResult.CreateSuccess(ToCloudFile(key, stored));
    }

    private CloudFile ToCloudFile(string key, SharedBucket.StoredObject stored)
        => new()
        {
            FileId = key,
            FileName = key[(key.LastIndexOf('/') + 1)..],
            StoragePath = key,
            ContentType = stored.ContentType,
            SizeBytes = stored.Bytes.LongLength,
            UploadedAt = stored.UploadedAt,
            Metadata = ImmutableDictionary<string, string>.Empty,
            Provider = provider
        };

    private sealed class ForwardOnlyStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();

        public override long Position
        {
            get => base.Position;
            set => throw new NotSupportedException();
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

public interface ICloudFileStorage
{
    CloudStorageProvider Provider { get; }

    // Single file operations

    Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default);

    // A null expected ETag requires the object to be absent, so two writers cannot overwrite one deterministic cache key.
    Task<UploadResult> UploadIfMatchAsync(
        FileUploadRequest request,
        string? expectedETag,
        CancellationToken cancellationToken = default)
        => Task.FromResult(UploadResult.CreateFailure("Conditional uploads are not supported by this storage provider."));

    Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default);

    // Upload progress tracking

    Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default);

    Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default);

    Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default);

    Task<Stream?> DownloadIfMatchAsync(string fileId, string expectedETag, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Conditional artifact reads are not supported by this storage provider.");


    Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default);

    Task<bool> DeleteIfMatchAsync(
        string fileId,
        string expectedETag,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    // Batch operations

    Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default);

    Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default);

    // Metadata operations

    Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudFile>> ListFilesAsync(
        string? folder = null,
        int maxResults = 1000,
        bool includeMetadata = true,
        CancellationToken cancellationToken = default);

    // URL operations

    Task<string?> GetPresignedUrlAsync(
        string fileId,
        TimeSpan? expiresIn = null,
        CancellationToken cancellationToken = default);

    Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
        string fileName,
        string contentType,
        TimeSpan? expiresIn = null,
        string? folder = null,
        CancellationToken cancellationToken = default);

    // Cleanup operations

    Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default);
}

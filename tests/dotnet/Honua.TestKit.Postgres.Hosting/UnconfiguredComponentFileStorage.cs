// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.TestKit.Postgres.Hosting;

/// <summary>
/// Satisfies attachment-store construction without pretending that a database-only
/// component host has object storage. Every storage access requires explicit composition.
/// </summary>
internal sealed class UnconfiguredComponentFileStorage : ICloudFileStorage
{
    public CloudStorageProvider Provider => throw Unconfigured();

    public Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<UploadResult> UploadIfMatchAsync(FileUploadRequest request, string? expectedETag, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<bool> DeleteIfMatchAsync(string fileId, string expectedETag, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<IReadOnlyList<CloudFile>> ListFilesAsync(string? folder = null, int maxResults = 1000, bool includeMetadata = true, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<string?> GetPresignedUrlAsync(string fileId, TimeSpan? expiresIn = null, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(string fileName, string contentType, TimeSpan? expiresIn = null, string? folder = null, CancellationToken cancellationToken = default)
        => throw Unconfigured();

    public Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
        => throw Unconfigured();

    private static NotSupportedException Unconfigured()
        => new("This component host has no object-storage provider. Register an explicit ICloudFileStorage to test storage operations.");
}

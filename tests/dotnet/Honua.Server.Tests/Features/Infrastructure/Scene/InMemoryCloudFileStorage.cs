// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

/// <summary>
/// Minimal in-memory <see cref="ICloudFileStorage"/> used by the scene asset
/// hydration/publish tests. Objects are keyed by the explicit
/// <see cref="FileUploadRequest.ObjectKeyOverride"/> the scene upload path always
/// supplies, so a deterministic prefix-scoped key round-trips through upload and
/// download exactly as it would against S3/Azure. Only the members the scene path
/// exercises are implemented; the rest throw so unexpected usage is loud.
/// </summary>
internal sealed class InMemoryCloudFileStorage : ICloudFileStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private int _downloadCount;
    private readonly TimeSpan _downloadDelay;

    public InMemoryCloudFileStorage(TimeSpan? downloadDelay = null)
    {
        _downloadDelay = downloadDelay ?? TimeSpan.Zero;
    }

    /// <summary>Number of <see cref="DownloadBytesAsync"/> calls observed.</summary>
    public int DownloadCount => Volatile.Read(ref _downloadCount);

    /// <summary>Object keys currently held, for assertions.</summary>
    public IReadOnlyCollection<string> Keys => _objects.Keys.ToArray();

    /// <summary>Overwrites an object's bytes to simulate a republish.</summary>
    public void Put(string key, byte[] bytes) => _objects[key] = bytes;

    public CloudStorageProvider Provider => CloudStorageProvider.Local;

    public async Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.ObjectKeyOverride
            ?? throw new InvalidOperationException("Scene uploads must supply an ObjectKeyOverride.");

        using var buffer = new MemoryStream();
        await request.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        _objects[key] = bytes;

        var cloudFile = new CloudFile
        {
            FileId = key,
            FileName = request.FileName,
            StoragePath = key,
            ContentType = request.ContentType,
            SizeBytes = bytes.Length,
            UploadedAt = DateTimeOffset.UtcNow,
            Provider = CloudStorageProvider.Local
        };
        return UploadResult.CreateSuccess(cloudFile);
    }

    public async Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _downloadCount);
        if (_downloadDelay > TimeSpan.Zero)
        {
            await Task.Delay(_downloadDelay, cancellationToken).ConfigureAwait(false);
        }
        return _objects.TryGetValue(fileId, out var bytes) ? bytes : null;
    }

    public Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
        => Task.FromResult<Stream?>(
            _objects.TryGetValue(fileId, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

    public Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
        => Task.FromResult(_objects.ContainsKey(fileId));

    // Unused by the scene asset path.
    public Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<CloudFile>> ListFilesAsync(
        string? folder = null,
        int maxResults = 1000,
        bool includeMetadata = true,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<string?> GetPresignedUrlAsync(
        string fileId,
        TimeSpan? expiresIn = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
        string fileName,
        string contentType,
        TimeSpan? expiresIn = null,
        string? folder = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

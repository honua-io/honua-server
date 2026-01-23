// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.FileStorage;

internal abstract class CloudFileStorageBase : ICloudFileStorage
{
    protected CloudFileStorageBase(ILogger logger, IUploadProgressStore progressStore)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ProgressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
    }

    protected ILogger Logger { get; }
    protected IUploadProgressStore ProgressStore { get; }
    protected ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> BatchIndex { get; } = new();
    protected ConcurrentDictionary<string, CancellationTokenSource> UploadCancellationTokens { get; } = new();

    public abstract CloudStorageProvider Provider { get; }

    public abstract Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default);

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

    public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        return ProgressStore.GetProgressAsync(uploadId, cancellationToken);
    }

    public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
        => ProgressStore.GetActiveUploadsAsync(cancellationToken);

    public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default)
        => CancelUploadInternalAsync(uploadId, cancellationToken);

    public abstract Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default);

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

    public abstract Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default);

    public virtual async Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var batchId = Guid.NewGuid().ToString("N");
        var uploadedFiles = new List<CloudFile>();
        var failedFiles = new Dictionary<string, string>();

        BatchIndex[batchId] = new ConcurrentDictionary<string, byte>();

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
                _ = BatchIndex[batchId].TryAdd(result.File.FileId, 0);
            }
            else
            {
                failedFiles[file.FileName] = result.ErrorMessage ?? "Unknown error";

                if (!request.ContinueOnError)
                {
                    foreach (var uploaded in uploadedFiles)
                    {
                        await DeleteBatchFileAsync(uploaded.FileId, cancellationToken);
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

    public virtual async Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);

        if (BatchIndex.TryRemove(batchId, out var fileIds))
        {
            var deletedCount = 0;
            foreach (var fileId in fileIds.Keys)
            {
                if (await DeleteBatchFileAsync(fileId, cancellationToken))
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

        return await DeleteByPrefixAsync(batchId, cancellationToken);
    }

    public abstract Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default);

    public abstract Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default);

    public abstract Task<IReadOnlyList<CloudFile>> ListFilesAsync(
        string? folder = null,
        int maxResults = 1000,
        CancellationToken cancellationToken = default);

    public abstract Task<string?> GetPresignedUrlAsync(
        string fileId,
        TimeSpan? expiresIn = null,
        CancellationToken cancellationToken = default);

    public abstract Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
        string fileName,
        string contentType,
        TimeSpan? expiresIn = null,
        string? folder = null,
        CancellationToken cancellationToken = default);

    public abstract Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default);

    protected virtual Task<bool> DeleteBatchFileAsync(string fileId, CancellationToken cancellationToken)
        => DeleteAsync(fileId, cancellationToken);

    protected virtual Task<int> DeleteByPrefixAsync(string batchId, CancellationToken cancellationToken)
        => Task.FromResult(0);

    protected async Task<bool> CancelUploadInternalAsync(string uploadId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);

        if (UploadCancellationTokens.TryRemove(uploadId, out var cancellationSource))
        {
            await cancellationSource.CancelAsync();
            cancellationSource.Dispose();

            var currentProgress = await ProgressStore.GetProgressAsync(uploadId, cancellationToken);
            if (currentProgress != null && currentProgress.Status == OperationStatus.Processing)
            {
                var cancelledProgress = currentProgress with
                {
                    Status = OperationStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Cancelled"
                };
                await ProgressStore.SetProgressAsync(uploadId, cancelledProgress, TimeSpan.FromMinutes(10), cancellationToken);
            }

            return true;
        }

        return false;
    }
}

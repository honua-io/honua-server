// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Internal;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

/// <summary>
/// Local filesystem implementation of cloud file storage for development and testing
/// </summary>
internal sealed class LocalFileStorage : CloudFileStorageBase
{
    private const int MutationLockStripeCount = 64;
    private readonly LocalStorageOptions _options;
    private readonly ConcurrentDictionary<string, CloudFile> _fileIndex;
    private readonly SemaphoreSlim[] _mutationLocks = Enumerable.Range(0, MutationLockStripeCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();
    private readonly string _basePath;
    private readonly string _metadataPath;
    private readonly IStorageDomainMutationFence? _domainFence;

    internal bool IsBoundToMutationFence(IStorageDomainMutationFence fence) => ReferenceEquals(_domainFence, fence);

    /// <summary>
    /// Creates a new local file storage instance
    /// </summary>
    /// <param name="options">Local storage options</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="progressStore">Upload progress store</param>
    /// <param name="domainFence">Optional host-owned fence covering physical mutations and metadata publication.</param>
    public LocalFileStorage(
        IOptions<LocalStorageOptions> options,
        ILogger<LocalFileStorage> logger,
        IUploadProgressStore progressStore,
        IStorageDomainMutationFence? domainFence = null)
        : base(logger, progressStore)
    {
        if (domainFence is not null && domainFence is not IStorageDomainObjectMutationFence)
            throw new ArgumentException("A fenced local provider requires object-specific reservation admission.", nameof(domainFence));
        _domainFence = domainFence;
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _basePath = string.IsNullOrWhiteSpace(_options.BasePath)
            ? Directory.GetCurrentDirectory()
            : Path.TrimEndingDirectorySeparator(_options.BasePath);
        if (_domainFence is not null && !Path.IsPathFullyQualified(_basePath))
            throw new ArgumentException("A fenced local provider requires an absolute host-owned storage root.", nameof(options));
        if (_domainFence is not null)
            _basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_basePath));
        // ".metadata" is a compile-time relative literal, so it cannot be rooted and this
        // combine can never silently drop _basePath.
        _metadataPath = Path.Join(_basePath, ".metadata");
        _fileIndex = new ConcurrentDictionary<string, CloudFile>();

        InitializeStorage();
    }

    /// <inheritdoc />
    public override CloudStorageProvider Provider => CloudStorageProvider.Local;

    /// <inheritdoc />
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
        await using var domainLease = _domainFence is null ? null
            : await _domainFence.EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        if (conditional && expectedETag is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag);
        }

        var stopwatch = Stopwatch.StartNew();
        var uploadId = request.UploadId;
        // Not a `using` declaration: this token source is published to UploadCancellationTokens
        // below and must outlive this method call so a concurrent CancelUploadAsync request (on
        // another request/thread) can look it up and cancel it. Ownership transfers to whichever
        // path removes it from the dictionary first; the finally block below disposes it once
        // this upload's own processing has removed its entry.
        var linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SerializedUploadProgressWriter? progressWriter = null;
        SemaphoreSlim? mutationLock = null;
        var mutationLockHeld = false;

        // Store cancellation token for potential cancellation
        UploadCancellationTokens[uploadId] = linkedCancellationSource;

        try
        {
            var totalBytes = request.SizeBytes ?? (request.Content.CanSeek ? request.Content.Length : 0);
            if (_domainFence is not null && (request.SizeBytes is not > 0 || string.IsNullOrWhiteSpace(request.ObjectKeyOverride)))
                throw new InvalidOperationException("Reserved local writes require an exact object key and positive declared length.");
            var initialProgress = UploadProgress.CreateInitial(uploadId, request.FileName, totalBytes, request.ContentType);

            // Store initial progress
            await ProgressStore.SetProgressAsync(uploadId, initialProgress, TimeSpan.FromHours(1), cancellationToken);

            // Report initial progress if callback provided
            ReportProgressToCaller(request.Progress, uploadId, initialProgress);

            string fileId;
            string storagePath;
            if (!string.IsNullOrWhiteSpace(request.ObjectKeyOverride))
            {
                ValidateObjectKeyOverride(request.ObjectKeyOverride);
                if (_domainFence is not null) ValidateReservedObjectKey(request.ObjectKeyOverride);
                fileId = request.ObjectKeyOverride;
                storagePath = request.ObjectKeyOverride;
            }
            else
            {
                fileId = GenerateFileId();
                storagePath = BuildStoragePath(fileId, request.FileName, request.Folder);
            }

            await using var objectLease = _domainFence is IStorageDomainObjectMutationFence objects
                ? await objects.EnterObjectMutationAsync(fileId, totalBytes, cancellationToken).ConfigureAwait(false) : null;
            if (_domainFence is not null && expectedETag is not null)
                throw new NotSupportedException("Reserved local objects are immutable and cannot be replaced.");
            var fullPath = GetSafeFullPath(storagePath);

            mutationLock = GetMutationLock(fileId);
            await mutationLock.WaitAsync(linkedCancellationSource.Token).ConfigureAwait(false);
            mutationLockHeld = true;

            if (conditional)
            {
                var current = _domainFence is null ? _fileIndex.GetValueOrDefault(fileId)
                    : await ReadAuthoritativeMetadataAsync(fileId, cancellationToken).ConfigureAwait(false);
                var preconditionMatches = expectedETag is null
                    ? current is null
                    : current is not null
                      && string.Equals(current.ETag, expectedETag, StringComparison.Ordinal);
                if (!preconditionMatches)
                {
                    throw new InvalidOperationException(
                        $"The object '{fileId}' changed before the conditional upload could commit.");
                }
            }

            // Ensure directory exists
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write file content with progress tracking
            string? contentHash;
            long sizeBytes;

            await using (var fileStream = new FileStream(fullPath, _domainFence is null ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var hashAlgorithm = SHA256.Create();
                await using var cryptoStream = new CryptoStream(fileStream, hashAlgorithm, CryptoStreamMode.Write, leaveOpen: true);

                // Wrap the source stream with progress tracking if progress callback provided
                Stream sourceStream = request.Content;
                if (request.Progress != null)
                {
                    var writer = CreateProgressWriter(uploadId);
                    progressWriter = writer;
                    var progressTracker = new Progress<UploadProgress>(progress =>
                    {
                        writer.Report(progress);
                        ReportProgressToCaller(request.Progress, uploadId, progress);
                    });
                    sourceStream = new ProgressTrackingStream(request.Content, progressTracker, uploadId, request.FileName, totalBytes);
                }

                if (_domainFence is null)
                    await sourceStream.CopyToAsync(cryptoStream, linkedCancellationSource.Token);
                else
                    await CopyReservedContentAsync(sourceStream, cryptoStream, totalBytes, linkedCancellationSource.Token).ConfigureAwait(false);
                await cryptoStream.FlushFinalBlockAsync(linkedCancellationSource.Token);
                if (_domainFence is not null) fileStream.Flush(flushToDisk: true);

                if (sourceStream != request.Content)
                {
                    await sourceStream.DisposeAsync();
                }

                contentHash = Convert.ToHexString(hashAlgorithm.Hash ?? []);
                sizeBytes = fileStream.Length;
            }
            if (_domainFence is not null) LocalStorageDurability.FlushDirectoryTree(directory!, _basePath);

            if (progressWriter != null)
            {
                await progressWriter.CloseAndDrainAsync().ConfigureAwait(false);
            }

            var uploadedAt = DateTimeOffset.UtcNow;
            var expiresAt = request.TimeToLive.HasValue
                ? uploadedAt.Add(request.TimeToLive.Value)
                : (DateTimeOffset?)null;

            var cloudFile = new CloudFile
            {
                FileId = fileId,
                FileName = request.FileName,
                StoragePath = storagePath,
                ContentType = request.ContentType,
                SizeBytes = sizeBytes,
                UploadedAt = uploadedAt,
                ExpiresAt = expiresAt,
                ContentHash = contentHash,
                // UploadId is caller-controlled correlation and may be reused after a
                // lost response. Each write needs an independent generation so a stale
                // conditional delete or overwrite cannot act on a replacement object.
                ETag = Guid.NewGuid().ToString("N"),
                Metadata = request.Metadata.Add("UploadId", uploadId),
                Provider = CloudStorageProvider.Local
            };

            await SaveMetadataAsync(cloudFile, cancellationToken);
            _fileIndex[fileId] = cloudFile;

            // Report completion
            var completedProgress = new UploadProgress
            {
                UploadId = uploadId,
                Status = OperationStatus.Completed,
                BytesUploaded = sizeBytes,
                TotalBytes = totalBytes,
                FileName = request.FileName,
                ContentType = request.ContentType,
                CloudFileId = fileId,
                StartedAt = initialProgress.StartedAt,
                CompletedAt = uploadedAt,
                CurrentPhase = "Upload completed"
            };
            await ProgressStore.SetProgressAsync(uploadId, completedProgress, TimeSpan.FromHours(1), cancellationToken);
            ReportProgressToCaller(request.Progress, uploadId, completedProgress);

            stopwatch.Stop();
            FileStorageLog.FileUploaded(
                Logger,
                request.FileName,
                sizeBytes,
                fileId,
                stopwatch.ElapsedMilliseconds);

            return UploadResult.CreateSuccess(cloudFile, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (linkedCancellationSource.Token.IsCancellationRequested)
        {
            stopwatch.Stop();
            if (progressWriter != null)
            {
                await progressWriter.CloseAndDrainAsync().ConfigureAwait(false);
            }

            var startedAt = DateTimeOffset.UtcNow - stopwatch.Elapsed;
            await ReportCancelledUploadAsync(uploadId, request, request.SizeBytes ?? 0, startedAt);
            throw;
        }
        catch (ArgumentException ex) when (string.Equals(ex.ParamName, "folder", StringComparison.Ordinal))
        {
            if (progressWriter != null)
            {
                await progressWriter.CloseAndDrainAsync().ConfigureAwait(false);
            }

            var startedAt = DateTimeOffset.UtcNow - stopwatch.Elapsed;
            var (progressMessage, resultMessage) = ResolveArgumentFailureMessages(ex);
            return await ReportFailedUploadAsync(
                uploadId,
                request,
                stopwatch,
                request.SizeBytes ?? 0,
                startedAt,
                ex,
                progressMessage,
                resultMessage);
        }
        // Intentionally generic: this is the top-level boundary for the upload operation
        // (after the specific cancellation/argument catches above) and must convert any
        // remaining failure — filesystem, hashing, or downstream progress-store errors —
        // into a reported failed-upload result instead of throwing out of the API.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (progressWriter != null)
            {
                await progressWriter.CloseAndDrainAsync().ConfigureAwait(false);
            }

            var startedAt = DateTimeOffset.UtcNow - stopwatch.Elapsed;
            return await ReportFailedUploadAsync(
                uploadId,
                request,
                stopwatch,
                request.SizeBytes ?? 0,
                startedAt,
                ex,
                "File upload failed",
                "File upload failed.");
        }
        finally
        {
            if (mutationLockHeld)
            {
                mutationLock!.Release();
            }

            // Clean up cancellation token
            UploadCancellationTokens.TryRemove(uploadId, out _);
            DeferredDisposal.Dispose(linkedCancellationSource);
        }
    }

    /// <inheritdoc />
    public override async Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        var cloudFile = await GetMetadataAsync(fileId, cancellationToken);
        if (cloudFile is null)
        {
            return null;
        }

        var fullPath = GetSafeFullPath(cloudFile.StoragePath);
        if (_domainFence is not null)
        {
            try { return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true); }
            catch (FileNotFoundException) { return null; }
        }
        if (!File.Exists(fullPath))
        {
            FileStorageLog.FileMissingOnDisk(Logger, fileId);
            return null;
        }

        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    }

    /// <inheritdoc />
    public override async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        await using var domainLease = _domainFence is null ? null
            : await _domainFence.EnterMutationAsync(cancellationToken).ConfigureAwait(false);

        await using var objectLease = _domainFence is IStorageDomainObjectMutationFence objects
            ? await objects.EnterObjectMutationAsync(fileId, null, cancellationToken).ConfigureAwait(false) : null;
        var mutationLock = GetMutationLock(fileId);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cloudFile = _domainFence is null
                ? _fileIndex.TryRemove(fileId, out var cached) ? cached : null
                : await ReadAuthoritativeMetadataAsync(fileId, cancellationToken).ConfigureAwait(false);
            if (cloudFile is null) return false;
            _fileIndex.TryRemove(fileId, out _);
            return await DeleteRemovedFileAsync(fileId, cloudFile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private async Task<bool> DeleteRemovedFileAsync(
        string fileId,
        CloudFile cloudFile,
        CancellationToken cancellationToken)
    {

        var fullPath = GetSafeFullPath(cloudFile.StoragePath);
        var metadataFile = GetMetadataFilePath(fileId);

        try
        {
            if (_domainFence is not null || File.Exists(fullPath))
            {
                File.Delete(fullPath);
                if (_domainFence is not null) LocalStorageDurability.FlushDirectoryTree(Path.GetDirectoryName(fullPath)!, _basePath);
            }

            if (_domainFence is not null || File.Exists(metadataFile))
            {
                File.Delete(metadataFile);
                if (_domainFence is not null) LocalStorageDurability.FlushDirectoryTree(_metadataPath, _basePath);
            }

            // Remove from batch index if present
            foreach (var batch in BatchIndex)
            {
                _ = batch.Value.TryRemove(fileId, out _);
            }

            // Clean up upload progress if exists
            if (cloudFile.Metadata.TryGetValue("UploadId", out var uploadId))
            {
                await ProgressStore.DeleteProgressAsync(uploadId, cancellationToken);
            }

            FileStorageLog.FileDeleted(Logger, fileId);
            return true;
        }
        // Intentionally generic: the try block spans two independent failure domains
        // (local filesystem deletes and the external progress store), and any failure from
        // either must roll back the in-memory index consistently and report a single bool
        // result rather than throwing out of a best-effort delete.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FileStorageLog.FileDeleteFailed(Logger, ex, fileId);
            // Re-add to index since deletion failed
            _ = _fileIndex.TryAdd(fileId, cloudFile);
            return false;
        }
    }

    public override async Task<bool> DeleteIfMatchAsync(
        string fileId,
        string expectedETag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        await using var domainLease = _domainFence is null ? null
            : await _domainFence.EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag);

        await using var objectLease = _domainFence is IStorageDomainObjectMutationFence objects
            ? await objects.EnterObjectMutationAsync(fileId, null, cancellationToken).ConfigureAwait(false) : null;
        var mutationLock = GetMutationLock(fileId);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _domainFence is null
                ? _fileIndex.GetValueOrDefault(fileId)
                : await ReadAuthoritativeMetadataAsync(fileId, cancellationToken).ConfigureAwait(false);
            if (current is null || !string.Equals(current.ETag, expectedETag, StringComparison.Ordinal)) return false;
            _fileIndex.TryRemove(fileId, out _);

            return await DeleteRemovedFileAsync(fileId, current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    /// <inheritdoc />
    public override Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        if (_domainFence is not null) return ReadAuthoritativeMetadataAsync(fileId, cancellationToken);
        _fileIndex.TryGetValue(fileId, out var cloudFile);
        return Task.FromResult(cloudFile);
    }

    /// <inheritdoc />
    public override async Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        return _domainFence is null ? _fileIndex.ContainsKey(fileId)
            : await ReadAuthoritativeMetadataAsync(fileId, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<CloudFile>> ListFilesAsync(
        string? folder = null,
        int maxResults = 1000,
        bool includeMetadata = true,
        CancellationToken cancellationToken = default)
    {
        if (_domainFence is not null) return ListAuthoritativeFilesAsync(folder, maxResults, cancellationToken);
        // The local index already holds the full CloudFile (including metadata and expiry), so
        // includeMetadata cannot save any work here and is intentionally ignored.
        _ = includeMetadata;
        var files = _fileIndex.Values
            .Where(f => string.IsNullOrEmpty(folder) || f.StoragePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.UploadedAt)
            .Take(maxResults)
            .ToList();

        return Task.FromResult<IReadOnlyList<CloudFile>>(files);
    }

    private async Task<IReadOnlyList<CloudFile>> ListAuthoritativeFilesAsync(string? folder, int maxResults,
        CancellationToken cancellationToken)
    {
        if (maxResults <= 0) return [];
        // This is a bounded catalog view, not a physical storage census: partial
        // objects and temporary metadata remain the recovery coordinator's concern.
        var latest = new PriorityQueue<CloudFile, DateTimeOffset>();
        foreach (var path in Directory.EnumerateFiles(_metadataPath, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await ReadMetadataRecordAsync(path, null, cancellationToken).ConfigureAwait(false);
            if (file is null || (!string.IsNullOrEmpty(folder)
                && !file.StoragePath.StartsWith(folder, StringComparison.Ordinal))) continue;
            latest.Enqueue(file, file.UploadedAt);
            if (latest.Count > maxResults) latest.Dequeue();
        }
        return latest.UnorderedItems.Select(item => item.Element).OrderByDescending(file => file.UploadedAt).ToArray();
    }

    /// <inheritdoc />
    public override Task<string?> GetPresignedUrlAsync(
        string fileId,
        TimeSpan? expiresIn = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        if (_domainFence is not null)
            throw new NotSupportedException("A fenced storage domain exposes content through bounded streams, not raw filesystem paths.");

        // For local storage, we return the file path as a "URL"
        // In production, cloud providers would return actual presigned URLs
        if (!_fileIndex.TryGetValue(fileId, out var cloudFile))
        {
            return Task.FromResult<string?>(null);
        }

        var fullPath = GetSafeFullPath(cloudFile.StoragePath);
        return Task.FromResult<string?>(ToAbsoluteFileUri(fullPath));
    }

    /// <inheritdoc />
    public override Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
        string fileName,
        string contentType,
        TimeSpan? expiresIn = null,
        string? folder = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePresignedUploadArguments(fileName, contentType, cancellationToken, checkCancellation: false);
        if (_domainFence is not null)
            throw new NotSupportedException("A fenced storage domain cannot expose an unfenced direct filesystem writer.");

        // For local storage, we generate a file ID and path
        // Real cloud implementations would provide presigned upload URLs
        var fileId = GenerateFileId();
        var storagePath = BuildStoragePath(fileId, fileName, folder);
        var fullPath = GetSafeFullPath(storagePath);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return Task.FromResult<(string Url, string FileId)?>(
            (ToAbsoluteFileUri(fullPath), fileId));
    }

    /// <inheritdoc />
    public override async Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expiredFiles = _fileIndex.Values
            .Where(f => f.ExpiresAt.HasValue && f.ExpiresAt.Value <= now)
            .ToList();

        // Not a `.Where(...).Count()`: DeleteIfMatchAsync is an awaited side-effecting deletion
        // attempted for every expired file, not a pure predicate, so every element must still
        // be visited regardless of its outcome; only the counting is conditional.
        var cleanedCount = 0;
        // codeql[cs/linq/missed-where]: awaits a delete for every expired file, so the count is not a pure filter
        foreach (var file in expiredFiles)
        {
            if (!string.IsNullOrWhiteSpace(file.ETag))
            {
                cleanedCount += await DeleteIfMatchAsync(file.FileId, file.ETag, cancellationToken)
                    .ConfigureAwait(false) ? 1 : 0;
            }
        }

        if (cleanedCount > 0)
        {
            FileStorageLog.ExpiredFilesCleaned(Logger, cleanedCount);
        }

        return cleanedCount;
    }

    private void InitializeStorage()
    {
        if (_options.CreateDirectoryIfNotExists && !Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
            FileStorageLog.StorageDirectoryCreated(Logger, _basePath);
        }

        if (!Directory.Exists(_metadataPath))
        {
            Directory.CreateDirectory(_metadataPath);
        }

        if (_domainFence is not null && (((File.GetAttributes(_basePath) | File.GetAttributes(_metadataPath))
            & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Reserved storage directories cannot be symbolic links.");

        // Load existing metadata
        LoadExistingMetadata();
    }

    private void LoadExistingMetadata()
    {
        if (!Directory.Exists(_metadataPath))
        {
            return;
        }

        var metadataFiles = Directory.GetFiles(_metadataPath, "*.json");
        foreach (var file in metadataFiles)
        {
            try
            {
                if (_domainFence is not null && ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(file).Length > 65536))
                    throw new IOException("Invalid immutable object metadata.");
                var json = File.ReadAllText(file);
                var cloudFile = JsonSerializer.Deserialize(json, FileStorageJsonContext.Default.CloudFile);
                if (_domainFence is not null && (cloudFile is null || cloudFile.StoragePath != cloudFile.FileId ||
                    GetMetadataFilePath(cloudFile.FileId) != file || string.IsNullOrWhiteSpace(cloudFile.ETag)))
                    throw new IOException("Immutable object metadata does not match the reserved key.");
                if (cloudFile is not null)
                {
                    // Metadata written before generation validators were introduced has no
                    // ETag. Give that on-disk generation a stable legacy validator at startup
                    // so expiry cleanup can remain conditional instead of either leaking it
                    // forever or risking deletion of a later replacement.
                    if (string.IsNullOrWhiteSpace(cloudFile.ETag))
                    {
                        cloudFile = cloudFile with
                        {
                            ETag = $"legacy-{cloudFile.UploadedAt.UtcDateTime.Ticks:x16}-{cloudFile.SizeBytes:x16}",
                        };
                    }

                    _fileIndex[cloudFile.FileId] = cloudFile;

                    // Rebuild batch index
                    if (cloudFile.Metadata.TryGetValue("BatchId", out var batchId))
                    {
                        var batchFiles = BatchIndex.GetOrAdd(batchId, _ => new ConcurrentDictionary<string, byte>());
                        _ = batchFiles.TryAdd(cloudFile.FileId, 0);
                    }
                }
            }
            // Intentionally generic: this is best-effort startup metadata loading over every
            // file in the metadata directory; a single corrupt or unreadable metadata file
            // must not abort loading the rest or prevent storage from initializing.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (_domainFence is not null) throw;
                FileStorageLog.MetadataLoadFailed(Logger, ex, file);
            }
        }

        FileStorageLog.MetadataLoaded(Logger, _fileIndex.Count);
    }

    private async Task SaveMetadataAsync(CloudFile cloudFile, CancellationToken cancellationToken)
    {
        var metadataFile = GetMetadataFilePath(cloudFile.FileId);
        var json = JsonSerializer.Serialize(cloudFile, FileStorageJsonContext.Default.CloudFile);
        if (_domainFence is null)
        {
            await File.WriteAllTextAsync(metadataFile, json, cancellationToken);
            return;
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        if (bytes.Length > 65536) throw new IOException("Immutable object metadata exceeds its reserved allowance.");
        var temporary = metadataFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        // Metadata becomes visible only after the complete record is durable. A
        // failed publication retains the charged temporary file for census.
        File.Move(temporary, metadataFile, overwrite: false);
        LocalStorageDurability.FlushDirectoryTree(_metadataPath, _basePath);
    }

    private static async Task CopyReservedContentAsync(Stream source, Stream destination, long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        long written = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (read > maximumBytes - written)
                throw new InvalidDataException("The object content exceeds its storage reservation.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
        }
        if (written != maximumBytes)
            throw new InvalidDataException("The object content does not match its declared length.");
    }

    private string GetMetadataFilePath(string fileId)
    {
        // Deterministic publish flows can use slash-bearing object keys as fileIds.
        // Encode separators so the metadata file lives flat under _metadataPath
        // and Directory.GetFiles(*.json) still finds it.
        var safeName = _domainFence is null ? fileId.Replace('/', '_').Replace('\\', '_')
            : Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fileId)));

        // Defence-in-depth: the slash/backslash replacement above already prevents
        // POSIX-style traversal, but it does not prevent a rooted result on platforms
        // where a bare "C:" prefix (no separator) makes a path rooted. Resolve and
        // verify containment the same way GetSafeFullPath does for storage paths.
        var candidate = Path.GetFullPath(Path.Join(_metadataPath, $"{safeName}.json"));
        var root = Path.GetFullPath(_metadataPath) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The resolved metadata path escapes the metadata directory.",
                nameof(fileId));
        }

        return candidate;
    }

    private Task<CloudFile?> ReadAuthoritativeMetadataAsync(string fileId, CancellationToken cancellationToken)
    {
        ValidateReservedObjectKey(fileId);
        var path = GetMetadataFilePath(fileId);
        return ReadMetadataRecordAsync(path, fileId, cancellationToken);
    }

    private async Task<CloudFile?> ReadMetadataRecordAsync(string path, string? expectedFileId, CancellationToken cancellationToken)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Invalid immutable object metadata.");
            await using var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (content.Length > 65536) throw new IOException("Invalid immutable object metadata.");
            var value = await JsonSerializer.DeserializeAsync(content, FileStorageJsonContext.Default.CloudFile, cancellationToken).ConfigureAwait(false);
            if (value is null || (expectedFileId is not null && value.FileId != expectedFileId)
                || value.StoragePath != value.FileId || value.SizeBytes < 0 || string.IsNullOrWhiteSpace(value.ETag))
                throw new IOException("Immutable object metadata does not match the reserved key.");
            ValidateReservedObjectKey(value.FileId);
            if (GetMetadataFilePath(value.FileId) != path)
                throw new IOException("Immutable object metadata does not match its file identity.");
            return value;
        }
        catch (FileNotFoundException) { return null; }
    }

    private static void ValidateObjectKeyOverride(string objectKey)
    {
        if (Path.IsPathRooted(objectKey))
        {
            throw new ArgumentException("Object key override must be a relative path.", nameof(objectKey));
        }

        if (objectKey.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException("Object key override contains invalid path characters.", nameof(objectKey));
        }

        var separators = new[] { '/', '\\' };
        var segments = objectKey.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new ArgumentException("Object key override must not contain relative traversal segments.", nameof(objectKey));
        }
    }

    private static void ValidateReservedObjectKey(string objectKey)
    {
        // One reservation must identify exactly one physical path. Reject aliases
        // and the private metadata namespace before acquiring object admission.
        if (objectKey.Length > 1024 || objectKey.Contains('\\') || Path.IsPathRooted(objectKey))
            throw new ArgumentException("Reserved object keys must be bounded canonical relative paths.", nameof(objectKey));
        var segments = objectKey.Split('/');
        if (segments.Length > 8 || segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".." || segment.StartsWith('.')))
            throw new ArgumentException("Reserved object keys cannot alias paths or use private directories.", nameof(objectKey));
    }

    private static string GenerateFileId() =>
        Guid.NewGuid().ToString("N");

    private SemaphoreSlim GetMutationLock(string fileId)
    {
        var hash = (uint)StringComparer.Ordinal.GetHashCode(fileId);
        return _mutationLocks[(int)(hash % MutationLockStripeCount)];
    }

    private static string BuildStoragePath(string fileId, string fileName, string? folder)
    {
        var extension = Path.GetExtension(fileName);
        var storageName = $"{fileId}{extension}";

        if (string.IsNullOrWhiteSpace(folder))
        {
            return storageName;
        }

        ValidateFolderPath(folder);

        // folder was just validated above to be relative (not rooted, no ".." segments),
        // so this combine cannot silently drop storageName.
        return Path.Join(folder, storageName);
    }

    /// <summary>
    /// Combines <paramref name="storagePath"/> with the base path and verifies the result is
    /// contained within the base directory. This is defence-in-depth: the denylist in
    /// <see cref="ValidateObjectKeyOverride"/> and <see cref="ValidateFolderPath"/> already
    /// blocks explicit traversal segments. Fenced domains additionally reject existing
    /// symbolic links; their namespace must remain exclusively owned by the host.
    /// </summary>
    private string GetSafeFullPath(string storagePath)
    {
        var combined = Path.GetFullPath(Path.Join(_basePath, storagePath));
        var root = Path.GetFullPath(_basePath) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The resolved storage path escapes the base directory.",
                nameof(storagePath));
        }

        if (_domainFence is not null)
        {
            ValidateReservedObjectKey(storagePath);
            var current = combined;
            while (true)
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Reserved storage paths cannot contain symbolic links.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (current == _basePath) break;
                current = Path.GetDirectoryName(current) ?? throw new IOException("The storage domain boundary disappeared.");
            }
        }

        return combined;
    }

    private static string ToAbsoluteFileUri(string path)
    {
        var absolutePath = Path.GetFullPath(path);
        return new Uri(absolutePath).AbsoluteUri;
    }

    private static void ValidateFolderPath(string folder)
    {
        if (Path.IsPathRooted(folder))
        {
            throw new ArgumentException("Folder must be a relative path.", nameof(folder));
        }

        if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException("Folder contains invalid path characters.", nameof(folder));
        }

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var segments = folder.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new ArgumentException("Folder must not contain relative traversal segments.", nameof(folder));
        }
    }
}

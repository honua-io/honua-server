// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

/// <summary>
/// Local filesystem implementation of cloud file storage for development and testing
/// </summary>
internal sealed class LocalFileStorage : CloudFileStorageBase
{
    private readonly LocalStorageOptions _options;
    private readonly ConcurrentDictionary<string, CloudFile> _fileIndex;
    private readonly string _basePath;
    private readonly string _metadataPath;

    /// <summary>
    /// Creates a new local file storage instance
    /// </summary>
    /// <param name="options">Local storage options</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="progressStore">Upload progress store</param>
    public LocalFileStorage(
        IOptions<LocalStorageOptions> options,
        ILogger<LocalFileStorage> logger,
        IUploadProgressStore progressStore)
        : base(logger, progressStore)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _basePath = string.IsNullOrWhiteSpace(_options.BasePath)
            ? Directory.GetCurrentDirectory()
            : Path.TrimEndingDirectorySeparator(_options.BasePath);
        // ".metadata" is a compile-time relative literal, so it cannot be rooted and this
        // combine can never silently drop _basePath.
        _metadataPath = Path.Join(_basePath, ".metadata");
        _fileIndex = new ConcurrentDictionary<string, CloudFile>();

        InitializeStorage();
    }

    /// <inheritdoc />
    public override CloudStorageProvider Provider => CloudStorageProvider.Local;

    /// <inheritdoc />
    public override async Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var uploadId = request.UploadId;
        // Not a `using` declaration: this token source is published to UploadCancellationTokens
        // below and must outlive this method call so a concurrent CancelUploadAsync request (on
        // another request/thread) can look it up and cancel it. Ownership transfers to whichever
        // path removes it from the dictionary first; the finally block below disposes it once
        // this upload's own processing has removed its entry.
        var linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SerializedUploadProgressWriter? progressWriter = null;

        // Store cancellation token for potential cancellation
        UploadCancellationTokens[uploadId] = linkedCancellationSource;

        try
        {
            var totalBytes = request.SizeBytes ?? (request.Content.CanSeek ? request.Content.Length : 0);
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
                fileId = request.ObjectKeyOverride;
                storagePath = request.ObjectKeyOverride;
            }
            else
            {
                fileId = GenerateFileId();
                storagePath = BuildStoragePath(fileId, request.FileName, request.Folder);
            }

            var fullPath = GetSafeFullPath(storagePath);

            // Ensure directory exists
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write file content with progress tracking
            string? contentHash;
            long sizeBytes;

            await using (var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
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

                await sourceStream.CopyToAsync(cryptoStream, linkedCancellationSource.Token);
                await cryptoStream.FlushFinalBlockAsync(linkedCancellationSource.Token);

                if (sourceStream != request.Content)
                {
                    await sourceStream.DisposeAsync();
                }

                contentHash = Convert.ToHexString(hashAlgorithm.Hash ?? []);
                sizeBytes = fileStream.Length;
            }

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
                Metadata = request.Metadata.Add("UploadId", uploadId),
                Provider = CloudStorageProvider.Local
            };

            _fileIndex[fileId] = cloudFile;
            await SaveMetadataAsync(cloudFile, cancellationToken);

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

        if (!_fileIndex.TryRemove(fileId, out var cloudFile))
        {
            return false;
        }

        var fullPath = GetSafeFullPath(cloudFile.StoragePath);
        var metadataFile = GetMetadataFilePath(fileId);

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            if (File.Exists(metadataFile))
            {
                File.Delete(metadataFile);
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
            _fileIndex[fileId] = cloudFile;
            return false;
        }
    }

    /// <inheritdoc />
    public override Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        _fileIndex.TryGetValue(fileId, out var cloudFile);
        return Task.FromResult(cloudFile);
    }

    /// <inheritdoc />
    public override Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        var exists = _fileIndex.ContainsKey(fileId);
        return Task.FromResult(exists);
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<CloudFile>> ListFilesAsync(
        string? folder = null,
        int maxResults = 1000,
        bool includeMetadata = true,
        CancellationToken cancellationToken = default)
    {
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

    /// <inheritdoc />
    public override Task<string?> GetPresignedUrlAsync(
        string fileId,
        TimeSpan? expiresIn = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

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

        // Not a `.Where(...).Count()`: DeleteAsync is an awaited side-effecting deletion
        // attempted for every expired file, not a pure predicate, so every element must still
        // be visited regardless of its outcome; only the counting is conditional.
        var cleanedCount = 0;
        foreach (var file in expiredFiles)
        {
            cleanedCount += await DeleteAsync(file.FileId, cancellationToken).ConfigureAwait(false) ? 1 : 0;
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
                var json = File.ReadAllText(file);
                var cloudFile = JsonSerializer.Deserialize(json, FileStorageJsonContext.Default.CloudFile);
                if (cloudFile is not null)
                {
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
                FileStorageLog.MetadataLoadFailed(Logger, ex, file);
            }
        }

        FileStorageLog.MetadataLoaded(Logger, _fileIndex.Count);
    }

    private async Task SaveMetadataAsync(CloudFile cloudFile, CancellationToken cancellationToken)
    {
        var metadataFile = GetMetadataFilePath(cloudFile.FileId);
        var json = JsonSerializer.Serialize(cloudFile, FileStorageJsonContext.Default.CloudFile);
        await File.WriteAllTextAsync(metadataFile, json, cancellationToken);
    }

    private string GetMetadataFilePath(string fileId)
    {
        // Deterministic publish flows can use slash-bearing object keys as fileIds.
        // Encode separators so the metadata file lives flat under _metadataPath
        // and Directory.GetFiles(*.json) still finds it.
        var safeName = fileId
            .Replace('/', '_')
            .Replace('\\', '_');

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

    private static string GenerateFileId() =>
        Guid.NewGuid().ToString("N");

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
    /// blocks explicit traversal segments, but a GetFullPath containment check also catches
    /// symlinks and any platform-specific path encoding tricks.
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

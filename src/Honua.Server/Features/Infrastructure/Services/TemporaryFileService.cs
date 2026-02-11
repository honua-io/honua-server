// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Services;

/// <summary>
/// Service for managing temporary file storage for image exports.
/// </summary>
public interface ITemporaryFileService
{
    /// <summary>
    /// Stores temporary file data and returns a public URL.
    /// </summary>
    Task<string> StoreTemporaryFileAsync(byte[] data, string contentType, TimeSpan? expiration = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves temporary file data by ID.
    /// </summary>
    Task<(byte[] data, string contentType)?> GetTemporaryFileAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up expired temporary files.
    /// </summary>
    Task CleanupExpiredFilesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Configuration options for temporary file storage.
/// </summary>
public sealed class TemporaryFileOptions
{
    public const string SectionName = "TemporaryFiles";

    /// <summary>
    /// Base directory for temporary file storage.
    /// </summary>
    public string StorageDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "honua-temp");

    /// <summary>
    /// Default expiration time for temporary files.
    /// </summary>
    public TimeSpan DefaultExpiration { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum file size in bytes.
    /// </summary>
    public long MaxFileSizeBytes { get; init; } = 50 * 1024 * 1024; // 50 MB

    /// <summary>
    /// Base URL for serving temporary files.
    /// </summary>
    public string? BaseUrl { get; init; }
}

/// <summary>
/// File system-based implementation of temporary file service.
/// </summary>
internal sealed partial class FileSystemTemporaryFileService : ITemporaryFileService
{
    private static readonly HashSet<string> _allowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/tiff",
        "application/octet-stream"
    };

    private static readonly string[] _imageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".tiff", ".tif", ""];

    private readonly TemporaryFileOptions _options;
    private readonly ILogger<FileSystemTemporaryFileService> _logger;

    public FileSystemTemporaryFileService(
        IOptions<TemporaryFileOptions> options,
        ILogger<FileSystemTemporaryFileService> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Ensure storage directory exists
        Directory.CreateDirectory(_options.StorageDirectory);
    }

    public async Task<string> StoreTemporaryFileAsync(
        byte[] data,
        string contentType,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        if (data.Length > _options.MaxFileSizeBytes)
        {
            throw new InvalidOperationException($"File size {data.Length} exceeds maximum allowed size {_options.MaxFileSizeBytes}");
        }

        if (!_allowedContentTypes.Contains(contentType))
        {
            throw new InvalidOperationException($"Content type '{contentType}' is not allowed. Allowed types: {string.Join(", ", _allowedContentTypes)}");
        }

        var fileId = Guid.NewGuid().ToString("N");
        var extension = GetFileExtension(contentType);
        var fileName = $"{fileId}{extension}";
        var filePath = Path.Combine(_options.StorageDirectory, fileName);

        var expirationTime = DateTimeOffset.UtcNow.Add(expiration ?? _options.DefaultExpiration);

        try
        {
            // Write file data
            await File.WriteAllBytesAsync(filePath, data, cancellationToken);

            // Write metadata file
            var metadataPath = Path.Combine(_options.StorageDirectory, $"{fileId}.meta");
            var metadataObj = new TemporaryFileMetadata
            {
                ContentType = contentType,
                ExpiresAt = expirationTime,
                OriginalSize = data.Length,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var metadata = System.Text.Json.JsonSerializer.Serialize(metadataObj, TemporaryFileMetadataJsonContext.Default.TemporaryFileMetadata);
            await File.WriteAllTextAsync(metadataPath, metadata, cancellationToken);

            // Generate public URL
            var baseUrl = _options.BaseUrl ?? "/temp";
            var publicUrl = $"{baseUrl.TrimEnd('/')}/{fileName}";

            LogTemporaryFileStored(_logger, fileId, data.Length, expirationTime);

            return publicUrl;
        }
        catch (Exception ex)
        {
            LogStoreFileFailed(_logger, fileId, ex);
            throw;
        }
    }

    public async Task<(byte[] data, string contentType)?> GetTemporaryFileAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Parse file ID from path if needed
            var actualFileId = Path.GetFileNameWithoutExtension(fileId);

            // Validate file ID is a hex GUID (strict format from StoreTemporaryFileAsync)
            if (actualFileId.Length != 32 || !actualFileId.All(c => char.IsAsciiHexDigit(c)))
            {
                return null;
            }

            // Defense-in-depth: verify resolved path stays within storage directory
            var resolvedBase = Path.GetFullPath(_options.StorageDirectory);
            var resolvedMeta = Path.GetFullPath(Path.Combine(_options.StorageDirectory, $"{actualFileId}.meta"));
            if (!resolvedMeta.StartsWith(resolvedBase, StringComparison.Ordinal))
            {
                return null;
            }

            var metadataPath = Path.Combine(_options.StorageDirectory, $"{actualFileId}.meta");
            if (!File.Exists(metadataPath))
            {
                return null;
            }

            // Read and check metadata
            var metadataJson = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            var metadata = System.Text.Json.JsonSerializer.Deserialize(metadataJson, TemporaryFileMetadataJsonContext.Default.TemporaryFileMetadata);

            if (metadata?.ExpiresAt < DateTimeOffset.UtcNow)
            {
                // File has expired, clean it up
                await DeleteFileAndMetadataAsync(actualFileId);
                return null;
            }

            // Find the actual file (try different extensions)
            var possibleExtensions = _imageExtensions;
            string? filePath = null;

            foreach (var ext in possibleExtensions)
            {
                var testPath = Path.Combine(_options.StorageDirectory, $"{actualFileId}{ext}");
                // Defense-in-depth: verify data file path also stays within storage directory
                var resolvedData = Path.GetFullPath(testPath);
                if (!resolvedData.StartsWith(resolvedBase, StringComparison.Ordinal))
                {
                    return null;
                }

                if (File.Exists(testPath))
                {
                    filePath = testPath;
                    break;
                }
            }

            if (filePath == null)
            {
                return null;
            }

            // Read file data
            var data = await File.ReadAllBytesAsync(filePath, cancellationToken);
            return (data, metadata?.ContentType ?? "application/octet-stream");
        }
        catch (Exception ex)
        {
            LogRetrieveFileFailed(_logger, fileId, ex);
            return null;
        }
    }

    public async Task CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var metadataFiles = Directory.GetFiles(_options.StorageDirectory, "*.meta");
            var now = DateTimeOffset.UtcNow;
            var cleanedCount = 0;

            foreach (var metadataPath in metadataFiles)
            {
                try
                {
                    var metadataJson = await File.ReadAllTextAsync(metadataPath, cancellationToken);
                    var metadata = System.Text.Json.JsonSerializer.Deserialize(metadataJson, TemporaryFileMetadataJsonContext.Default.TemporaryFileMetadata);

                    if (metadata?.ExpiresAt < now)
                    {
                        var fileId = Path.GetFileNameWithoutExtension(metadataPath);
                        await DeleteFileAndMetadataAsync(fileId);
                        cleanedCount++;
                    }
                }
                catch (Exception ex)
                {
                    LogProcessMetadataFailed(_logger, metadataPath, ex);
                }
            }

            if (cleanedCount > 0)
            {
                LogExpiredFilesCleanedUp(_logger, cleanedCount);
            }
        }
        catch (Exception ex)
        {
            LogCleanupFailed(_logger, ex);
        }
    }

    private async Task DeleteFileAndMetadataAsync(string fileId)
    {
        try
        {
            // Delete metadata file
            var metadataPath = Path.Combine(_options.StorageDirectory, $"{fileId}.meta");
            if (File.Exists(metadataPath))
            {
                File.Delete(metadataPath);
            }

            // Delete data file (try different extensions)
            var possibleExtensions = _imageExtensions;
            foreach (var ext in possibleExtensions)
            {
                var filePath = Path.Combine(_options.StorageDirectory, $"{fileId}{ext}");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LogDeleteFileFailed(_logger, fileId, ex);
        }
    }

    private static string GetFileExtension(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/tiff" => ".tif",
            _ => ""
        };
    }

    [LoggerMessage(EventId = 4500, Level = LogLevel.Information,
        Message = "Stored temporary file {FileId} ({Size} bytes, expires {ExpiresAt})")]
    private static partial void LogTemporaryFileStored(ILogger logger, string fileId, int size, DateTimeOffset expiresAt);

    [LoggerMessage(EventId = 4501, Level = LogLevel.Information,
        Message = "Cleaned up {CleanedCount} expired temporary files")]
    private static partial void LogExpiredFilesCleanedUp(ILogger logger, int cleanedCount);

    [LoggerMessage(EventId = 4502, Level = LogLevel.Error,
        Message = "Failed to store temporary file {FileId}")]
    private static partial void LogStoreFileFailed(ILogger logger, string fileId, Exception exception);

    [LoggerMessage(EventId = 4503, Level = LogLevel.Error,
        Message = "Failed to retrieve temporary file {FileId}")]
    private static partial void LogRetrieveFileFailed(ILogger logger, string fileId, Exception exception);

    [LoggerMessage(EventId = 4504, Level = LogLevel.Warning,
        Message = "Failed to process metadata file {MetadataPath}")]
    private static partial void LogProcessMetadataFailed(ILogger logger, string metadataPath, Exception exception);

    [LoggerMessage(EventId = 4505, Level = LogLevel.Error,
        Message = "Failed to cleanup expired temporary files")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4506, Level = LogLevel.Warning,
        Message = "Failed to delete temporary file {FileId}")]
    private static partial void LogDeleteFileFailed(ILogger logger, string fileId, Exception exception);

    [JsonSerializable(typeof(TemporaryFileMetadata))]
    private sealed partial class TemporaryFileMetadataJsonContext : JsonSerializerContext
    {
    }

    private sealed class TemporaryFileMetadata
    {
        public string? ContentType { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public long OriginalSize { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}

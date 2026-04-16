// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.Services;

/// <summary>
/// Uses shared cloud file storage for temporary files when the configured file-storage
/// provider is remote, and falls back to node-local storage for local-only deployments.
/// </summary>
internal sealed class CloudBackedTemporaryFileService : ITemporaryFileService, IDisposable
{
    private const string TemporaryFolder = "temporary-files";
    private const string AuthorizedPrincipalMetadataKey = "honua-temp-principal";
    private const string CloudTemporaryFileKeyPrefix = "honua:temporary-files:cloud:";
    private const string CloudTemporaryQuotaLeaseKey = "honua:temporary-files:cloud:quota";
    private const long MetadataOverheadBytes = 1024;
    private static readonly TimeSpan CloudTemporaryQuotaLeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloudTemporaryQuotaLeaseRetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/tiff",
        "application/pdf",
        "application/octet-stream"
    };

    private readonly FileSystemTemporaryFileService _localFileService;
    private readonly ICloudFileStorage _cloudFileStorage;
    private readonly TemporaryFileOptions _options;
    private readonly ILogger<CloudBackedTemporaryFileService> _logger;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly IDistributedCache? _distributedCache;
    private readonly IConnectionMultiplexer? _redis;
    private readonly SemaphoreSlim _cloudWriteGate = new(1, 1);

    public CloudBackedTemporaryFileService(
        FileSystemTemporaryFileService localFileService,
        ICloudFileStorage cloudFileStorage,
        IOptions<TemporaryFileOptions> options,
        ILogger<CloudBackedTemporaryFileService> logger,
        IHttpContextAccessor? httpContextAccessor = null,
        IDistributedCache? distributedCache = null,
        IConnectionMultiplexer? redis = null)
    {
        _localFileService = localFileService ?? throw new ArgumentNullException(nameof(localFileService));
        _cloudFileStorage = cloudFileStorage ?? throw new ArgumentNullException(nameof(cloudFileStorage));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpContextAccessor = httpContextAccessor;
        _distributedCache = distributedCache;
        _redis = redis;
    }

    private bool UseCloudStorage => _cloudFileStorage.Provider != CloudStorageProvider.Local;

    public async Task<string> StoreTemporaryFileAsync(
        byte[] data,
        string contentType,
        TimeSpan? expiration = null,
        ClaimsPrincipal? principal = null,
        CancellationToken cancellationToken = default)
    {
        principal ??= _httpContextAccessor?.HttpContext?.User;

        if (!UseCloudStorage)
        {
            return await _localFileService.StoreTemporaryFileAsync(
                data,
                contentType,
                expiration,
                principal,
                cancellationToken).ConfigureAwait(false);
        }

        ValidateCloudStorageRequest(data, contentType);

        await _cloudWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var quotaLease = await AcquireCloudQuotaLeaseAsync(cancellationToken).ConfigureAwait(false);
            using var quotaLeaseRenewalCts = quotaLease is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var processingCts = quotaLease is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quotaLease.LeaseLostToken);
            var quotaLeaseRenewalTask = quotaLease is null
                ? Task.CompletedTask
                : quotaLease.MaintainLeaseAsync(
                    TimeSpan.FromMilliseconds(CloudTemporaryQuotaLeaseDuration.TotalMilliseconds / 3d),
                    quotaLeaseRenewalCts!.Token);
            var processingToken = processingCts?.Token ?? cancellationToken;

            try
            {
                await _cloudFileStorage.CleanupExpiredFilesAsync(processingToken).ConfigureAwait(false);
                await EnsureCloudStorageCapacityAsync(data.Length, processingToken).ConfigureAwait(false);

                var extension = GetFileExtension(contentType);
                var uploadResult = await _cloudFileStorage.UploadAsync(
                    new ByteArrayUploadRequest
                    {
                        Content = data,
                        FileName = $"temporary-file{extension}",
                        ContentType = contentType,
                        TimeToLive = expiration ?? _options.DefaultExpiration,
                        Folder = TemporaryFolder,
                        Metadata = BuildCloudMetadata(principal)
                    },
                    processingToken).ConfigureAwait(false);

                if (!uploadResult.Success || uploadResult.File is null)
                {
                    throw new InvalidOperationException(uploadResult.ErrorMessage ?? "Failed to store temporary file in shared storage.");
                }

                var publicToken = await CreatePublicTokenAsync(
                    uploadResult.File.FileId,
                    expiration ?? _options.DefaultExpiration,
                    processingToken).ConfigureAwait(false);
                var publicFileName = $"{publicToken}{extension}";
                var baseUrl = _options.BaseUrl ?? "/temp";
                var publicUrl = $"{baseUrl.TrimEnd('/')}/{publicFileName}";

                _logger.LogInformation(
                    "Stored temporary file {FileId} in shared {Provider} storage ({Size} bytes, expires {ExpiresAt})",
                    uploadResult.File.FileId,
                    _cloudFileStorage.Provider,
                    uploadResult.File.SizeBytes,
                    uploadResult.File.ExpiresAt);

                return publicUrl;
            }
            catch (OperationCanceledException) when (quotaLease?.LeaseLostToken.IsCancellationRequested == true
                                                    && !cancellationToken.IsCancellationRequested)
            {
                throw new TemporaryStorageLimitExceededException(
                    "Temporary file coordination was lost while writing to shared storage. Please retry shortly.",
                    _options.StorageFullRetryAfterSeconds);
            }
            finally
            {
                if (quotaLeaseRenewalCts != null)
                {
                    quotaLeaseRenewalCts.Cancel();
                    await quotaLeaseRenewalTask.ConfigureAwait(false);
                    await quotaLease!.ReleaseAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _cloudWriteGate.Release();
        }
    }

    public async Task<(byte[] data, string contentType)?> GetTemporaryFileAsync(
        string fileId,
        ClaimsPrincipal? principal = null,
        CancellationToken cancellationToken = default)
    {
        principal ??= _httpContextAccessor?.HttpContext?.User;

        if (!UseCloudStorage)
        {
            return await _localFileService.GetTemporaryFileAsync(
                fileId,
                principal,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var publicToken = Path.GetFileNameWithoutExtension(fileId);
            if (string.IsNullOrWhiteSpace(publicToken))
            {
                return null;
            }

            var cloudObjectKey = await ResolveCloudObjectKeyAsync(publicToken, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(cloudObjectKey))
            {
                return null;
            }

            var metadata = await _cloudFileStorage.GetMetadataAsync(cloudObjectKey, cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                await DeletePublicTokenAsync(publicToken, CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            if (metadata.ExpiresAt.HasValue && metadata.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                _ = await _cloudFileStorage.DeleteAsync(cloudObjectKey, CancellationToken.None).ConfigureAwait(false);
                await DeletePublicTokenAsync(publicToken, CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            if (!IsPrincipalAuthorized(metadata.Metadata, principal))
            {
                return null;
            }

            var data = await _cloudFileStorage.DownloadBytesAsync(cloudObjectKey, cancellationToken).ConfigureAwait(false);
            if (data is null)
            {
                await DeletePublicTokenAsync(publicToken, CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            return (data, metadata.ContentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve temporary file {FileId} from shared {Provider} storage", fileId, _cloudFileStorage.Provider);
            return null;
        }
    }

    public async Task CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
    {
        if (!UseCloudStorage)
        {
            await _localFileService.CleanupExpiredFilesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _ = await _cloudFileStorage.CleanupExpiredFilesAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ValidateCloudStorageRequest(byte[] data, string contentType)
    {
        if (data.Length > _options.MaxFileSizeBytes)
        {
            throw new InvalidOperationException($"File size {data.Length} exceeds maximum allowed size {_options.MaxFileSizeBytes}");
        }

        if (!AllowedContentTypes.Contains(contentType))
        {
            throw new InvalidOperationException($"Content type '{contentType}' is not allowed. Allowed types: {string.Join(", ", AllowedContentTypes)}");
        }
    }

    private async Task EnsureCloudStorageCapacityAsync(long incomingDataSizeBytes, CancellationToken cancellationToken)
    {
        if (_options.MaxFileCount <= 0 && _options.MaxTotalStorageBytes <= 0)
        {
            return;
        }

        var maxResults = _options.MaxTotalStorageBytes > 0
            ? int.MaxValue
            : Math.Max(_options.MaxFileCount > 0 ? _options.MaxFileCount + 1 : 0, 1000);
        var files = await _cloudFileStorage.ListFilesAsync(TemporaryFolder, maxResults, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var activeFiles = files.Where(file => !file.ExpiresAt.HasValue || file.ExpiresAt.Value > now).ToArray();

        if (_options.MaxFileCount > 0 && activeFiles.Length >= _options.MaxFileCount)
        {
            _logger.LogWarning(
                "Temporary file storage file-count limit reached in shared {Provider} storage: {CurrentFileCount} >= {MaxFileCount}",
                _cloudFileStorage.Provider,
                activeFiles.Length,
                _options.MaxFileCount);
            throw new TemporaryStorageLimitExceededException(
                $"Temporary file storage has reached the maximum file count ({_options.MaxFileCount}).",
                _options.StorageFullRetryAfterSeconds);
        }

        var projectedTotalBytes = activeFiles.Sum(static file => file.SizeBytes) + incomingDataSizeBytes + MetadataOverheadBytes;
        if (_options.MaxTotalStorageBytes > 0 && projectedTotalBytes > _options.MaxTotalStorageBytes)
        {
            _logger.LogWarning(
                "Temporary file storage capacity exceeded in shared {Provider} storage: {ProjectedBytes} > {MaxBytes}",
                _cloudFileStorage.Provider,
                projectedTotalBytes,
                _options.MaxTotalStorageBytes);
            throw new TemporaryStorageLimitExceededException(
                $"Temporary file storage has reached the maximum capacity ({_options.MaxTotalStorageBytes} bytes).",
                _options.StorageFullRetryAfterSeconds);
        }
    }

    private static ImmutableDictionary<string, string> BuildCloudMetadata(ClaimsPrincipal? principal)
    {
        var principalKey = ResolvePrincipalKey(principal);
        if (string.IsNullOrWhiteSpace(principalKey))
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        return ImmutableDictionary<string, string>.Empty.Add(AuthorizedPrincipalMetadataKey, principalKey);
    }

    private static bool IsPrincipalAuthorized(IReadOnlyDictionary<string, string> metadata, ClaimsPrincipal? principal)
    {
        if (!TryGetMetadataValue(metadata, AuthorizedPrincipalMetadataKey, out var authorizedPrincipalKey)
            || string.IsNullOrWhiteSpace(authorizedPrincipalKey))
        {
            return true;
        }

        var principalKey = ResolvePrincipalKey(principal);
        return string.Equals(principalKey, authorizedPrincipalKey, StringComparison.Ordinal);
    }

    private static bool TryGetMetadataValue(IReadOnlyDictionary<string, string> metadata, string key, out string? value)
    {
        if (metadata.TryGetValue(key, out var directValue))
        {
            value = directValue;
            return true;
        }

        foreach (var pair in metadata)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? ResolvePrincipalKey(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.Identity.Name;
    }

    private static string GetFileExtension(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/tiff" => ".tif",
            "application/pdf" => ".pdf",
            _ => string.Empty
        };
    }

    private async Task<string> CreatePublicTokenAsync(
        string cloudObjectKey,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        var publicToken = EncodeCloudObjectKey(cloudObjectKey);

        if (_distributedCache != null)
        {
            await _distributedCache.SetStringAsync(
                GetCloudTemporaryFileCacheKey(publicToken),
                cloudObjectKey,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = retention
                },
                cancellationToken).ConfigureAwait(false);
        }

        return publicToken;
    }

    private async Task<string?> ResolveCloudObjectKeyAsync(string publicToken, CancellationToken cancellationToken)
    {
        if (_distributedCache != null)
        {
            var cachedObjectKey = await _distributedCache.GetStringAsync(
                GetCloudTemporaryFileCacheKey(publicToken),
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cachedObjectKey))
            {
                return cachedObjectKey;
            }
        }

        return TryDecodeCloudObjectKey(publicToken, out var decodedObjectKey)
            ? decodedObjectKey
            : null;
    }

    private async Task<RedisLeaseCoordinator?> AcquireCloudQuotaLeaseAsync(CancellationToken cancellationToken)
    {
        if (_redis == null)
        {
            _logger.LogWarning(
                "Rejecting shared temporary file write because Redis coordination is required for cloud-backed storage quotas.");
            throw new TemporaryStorageLimitExceededException(
                "Temporary file coordination requires Redis when using shared cloud storage. Please configure Redis and retry.",
                _options.StorageFullRetryAfterSeconds);
        }

        if (!_redis.IsConnected)
        {
            _logger.LogWarning(
                "Rejecting shared temporary file write because Redis coordination is unavailable for cloud-backed storage.");
            throw new TemporaryStorageLimitExceededException(
                "Temporary file coordination is currently unavailable. Please retry shortly.",
                _options.StorageFullRetryAfterSeconds);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var leaseCoordinator = new RedisLeaseCoordinator(_redis, CloudTemporaryQuotaLeaseKey, CloudTemporaryQuotaLeaseDuration);
            if (await leaseCoordinator.TryAcquireOrExtendAsync().ConfigureAwait(false))
            {
                return leaseCoordinator;
            }

            leaseCoordinator.Dispose();
            if (!_redis.IsConnected)
            {
                _logger.LogWarning(
                    "Rejecting shared temporary file write because Redis coordination was lost while enforcing cloud-backed storage quotas.");
                throw new TemporaryStorageLimitExceededException(
                    "Temporary file coordination is currently unavailable. Please retry shortly.",
                    _options.StorageFullRetryAfterSeconds);
            }

            await Task.Delay(CloudTemporaryQuotaLeaseRetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task DeletePublicTokenAsync(string publicToken, CancellationToken cancellationToken)
    {
        return _distributedCache == null
            ? Task.CompletedTask
            : _distributedCache.RemoveAsync(GetCloudTemporaryFileCacheKey(publicToken), cancellationToken);
    }

    private static string GetCloudTemporaryFileCacheKey(string publicToken)
    {
        return CloudTemporaryFileKeyPrefix + publicToken;
    }

    private static string EncodeCloudObjectKey(string cloudObjectKey)
    {
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(cloudObjectKey));
    }

    private static bool TryDecodeCloudObjectKey(string publicToken, out string? cloudObjectKey)
    {
        try
        {
            cloudObjectKey = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(publicToken));
            return !string.IsNullOrWhiteSpace(cloudObjectKey);
        }
        catch (FormatException)
        {
            cloudObjectKey = null;
            return false;
        }
    }

    public void Dispose()
    {
        _cloudWriteGate.Dispose();
    }
}

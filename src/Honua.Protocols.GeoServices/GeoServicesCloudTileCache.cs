// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.ServiceDefaults;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Helpers for optional storage-backed tile caching across GeoServices adapters.
/// </summary>
internal static class GeoServicesCloudTileCache
{
    private const string DefaultContentType = "application/octet-stream";

    internal readonly record struct Hit(byte[] Data, string ContentType);

    private readonly record struct GenerationObservation(bool IsFresh, bool Exists, string? ETag);

    /// <summary>
    /// Generated tile-cache serve-path hit/miss counters (#2661). Recorded here rather than in the
    /// per-protocol handlers so every generated-cache read is counted exactly once, and only when
    /// the cache was actually consulted (a disabled or unconfigured store is neither a hit nor a
    /// miss).
    /// </summary>
    private static readonly System.Diagnostics.Metrics.Counter<long> CacheHits =
        HonuaTelemetry.Meter.CreateCounter<long>(
            "honua.tile.cache.hits",
            "tiles",
            "Number of generated tile-cache serve-path hits.");

    private static readonly System.Diagnostics.Metrics.Counter<long> CacheMisses =
        HonuaTelemetry.Meter.CreateCounter<long>(
            "honua.tile.cache.misses",
            "tiles",
            "Number of generated tile-cache serve-path misses.");

    internal static async Task<Hit?> TryReadAsync(
        ICloudFileStorage? storage,
        CloudStorageOptions? storageOptions,
        string objectKey,
        CancellationToken cancellationToken,
        ITileCacheKeyIndex? keyIndex = null,
        string? tenantScope = null)
    {
        if (storage is null || storageOptions?.Enabled == false)
        {
            return null;
        }

        var hit = await TryReadCoreAsync(
            storage,
            objectKey,
            cancellationToken,
            keyIndex,
            tenantScope).ConfigureAwait(false);

        var tags = new TagList { { "protocol", "geoservices" } };
        if (hit is null)
        {
            CacheMisses.Add(1, tags);
        }
        else
        {
            CacheHits.Add(1, tags);
        }

        return hit;
    }

    private static async Task<Hit?> TryReadCoreAsync(
        ICloudFileStorage storage,
        string objectKey,
        CancellationToken cancellationToken,
        ITileCacheKeyIndex? keyIndex,
        string? tenantScope)
    {
        try
        {
            if (keyIndex is { IsEnabled: true }
                && await keyIndex.IsExpiredAsync(objectKey, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var metadata = await storage.GetMetadataAsync(objectKey, cancellationToken).ConfigureAwait(false);
            if (metadata is null ||
                (metadata.ExpiresAt.HasValue && metadata.ExpiresAt.Value <= DateTimeOffset.UtcNow))
            {
                return null;
            }

            var data = await storage.DownloadBytesAsync(objectKey, cancellationToken).ConfigureAwait(false);
            if (data is not { Length: > 0 })
            {
                return null;
            }

            // Close the read/download race with an operator expiration. A marker created while
            // object storage was being read must still turn this request into a cache miss.
            if (keyIndex is { IsEnabled: true }
                && await keyIndex.IsExpiredAsync(objectKey, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            // The download can cross the object's TTL after the initial metadata check. Recheck
            // against the same authoritative expiration before recording access, so an access
            // update cannot resurrect index state that TTL pruning just removed.
            if (metadata.ExpiresAt.HasValue && metadata.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            {
                return null;
            }

            var contentType = string.IsNullOrWhiteSpace(metadata.ContentType)
                ? DefaultContentType
                : metadata.ContentType;

            // Cache hit: refresh the tile's last-access score so hot tiles survive LRU eviction (#1917).
            if (keyIndex is { IsEnabled: true })
            {
                await keyIndex.RecordAccessIfCurrentAsync(
                    objectKey,
                    data.LongLength,
                    metadata.ExpiresAt,
                    tenantScope,
                    metadata.ETag,
                    cancellationToken).ConfigureAwait(false);
            }

            return new Hit(data, contentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Cache reads are opportunistic: a storage-backend failure must not fail the
            // tile request (caller falls back to regenerating the tile). Record the
            // exception on the current span so the failure is still observable in
            // telemetry rather than silently disappearing.
            HonuaTelemetry.RecordException(Activity.Current, ex);
            return null;
        }
    }

    internal static async Task TryWriteAsync(
        ICloudFileStorage? storage,
        CloudStorageOptions? storageOptions,
        string objectKey,
        byte[] data,
        string contentType,
        string fileName,
        ImmutableDictionary<string, string> metadata,
        CancellationToken cancellationToken,
        ITileCacheKeyIndex? keyIndex = null,
        string? tenantScope = null)
    {
        if (storage is null || storageOptions?.Enabled == false || data.Length == 0)
        {
            return;
        }

        try
        {
            async Task UploadAndRecordAsync(TileCacheMutationContext mutationContext)
            {
                var mutationToken = mutationContext.CancellationToken;
                var observation = await ObserveGenerationAsync(
                        storage,
                        keyIndex,
                        objectKey,
                        mutationToken).ConfigureAwait(false);
                if (observation.IsFresh)
                {
                    // Another waiter committed the same cold tile while this request waited for
                    // the mutation fence. Reuse that generation instead of serializing another
                    // upload behind it.
                    return;
                }

                if (observation.Exists && string.IsNullOrWhiteSpace(observation.ETag))
                {
                    throw new InvalidOperationException(
                        $"The stale tile '{objectKey}' has no provider ETag for a generation-fenced replacement.");
                }

                var ttl = ResolveTileCacheTtl(storageOptions);
                var expiresAt = DateTimeOffset.UtcNow.Add(ttl);
                using var stream = new MemoryStream(data, writable: false);
                mutationContext.LeaseLostToken.ThrowIfCancellationRequested();
                var upload = await storage.UploadIfMatchAsync(new FileUploadRequest
                {
                    Content = stream,
                    FileName = fileName,
                    ContentType = contentType,
                    SizeBytes = data.LongLength,
                    TimeToLive = ttl,
                    Metadata = metadata,
                    ObjectKeyOverride = objectKey,
                    EnableChunkedUpload = false
                }, observation.ETag, mutationToken).ConfigureAwait(false);

                if (!upload.Success)
                {
                    return;
                }

                var uploadedETag = upload.File?.ETag;

                // Providers stamp the authoritative expiration after the upload completes (for
                // example, local storage does so after copying the content). Prefer that value so
                // Redis never prunes lifecycle state while the stored object is still valid.
                var recordedExpiresAt = upload.File?.ExpiresAt ?? expiresAt;

                // Newly stored tile: record it in the live LRU index so the evictor can
                // quota-manage it and lifecycle deletion can fence this exact write.
                if (keyIndex is { IsEnabled: true })
                {
                    try
                    {
                        if (mutationContext.TryRecordWriteIfLeaseOwnedAsync is { } tryRecordWrite)
                        {
                            if (string.IsNullOrWhiteSpace(uploadedETag))
                            {
                                throw new InvalidOperationException(
                                    $"The uploaded tile '{objectKey}' has no provider ETag for a generation-fenced lifecycle commit.");
                            }

                            var committed = await tryRecordWrite(
                                new TileCacheWriteRegistration(
                                    data.LongLength,
                                    recordedExpiresAt,
                                    tenantScope,
                                    uploadedETag),
                                mutationToken).ConfigureAwait(false);
                            if (!committed)
                            {
                                throw new InvalidOperationException(
                                    $"The mutation lease for tile '{objectKey}' changed before its lifecycle state was committed.");
                            }
                        }
                        else
                        {
                            await keyIndex.RecordWriteAsync(
                                objectKey,
                                data.LongLength,
                                recordedExpiresAt,
                                tenantScope,
                                mutationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        // Lease loss means another replica may already own this key. Never let
                        // compensation from the old owner delete that replica's generation.
                        mutationContext.LeaseLostToken.ThrowIfCancellationRequested();

                        // Upload and lifecycle-state commit are one serialized cache mutation.
                        // If Redis cannot make the object discoverable to eviction/lifecycle
                        // readers, remove the just-uploaded bytes before releasing the fence.
                        var storageRolledBack = false;
                        try
                        {
                            // Caller cancellation must not interrupt compensation. Lease loss
                            // must: at that point deleting this key could remove a newer owner's
                            // generation. Renewal continues until this bounded cleanup returns.
                            if (string.IsNullOrWhiteSpace(uploadedETag))
                            {
                                throw new InvalidOperationException(
                                    $"The uploaded tile '{objectKey}' has no provider ETag for generation-safe rollback.");
                            }

                            storageRolledBack = await storage.DeleteIfMatchAsync(
                                objectKey,
                                uploadedETag,
                                mutationContext.LeaseLostToken).ConfigureAwait(false);
                            if (!storageRolledBack)
                            {
                                var current = await storage.GetMetadataAsync(
                                        objectKey,
                                        mutationContext.LeaseLostToken)
                                    .ConfigureAwait(false);
                                storageRolledBack = current is null;

                                // A different ETag proves a newer owner replaced these bytes.
                                // Its generation is healthy and must neither be deleted nor have
                                // its lifecycle state removed by this stale compensation path.
                                if (current is not null
                                    && !string.Equals(current.ETag, uploadedETag, StringComparison.Ordinal))
                                {
                                    return;
                                }
                            }

                            if (!storageRolledBack)
                            {
                                throw new InvalidOperationException(
                                    $"The uploaded tile '{objectKey}' remained in storage after its lifecycle-state commit failed.");
                            }
                        }
                        catch (Exception rollbackException) when (rollbackException is not OutOfMemoryException)
                        {
                            HonuaTelemetry.RecordException(Activity.Current, rollbackException);
                        }

                        if (storageRolledBack
                            && mutationContext.TryRemoveIndexIfLeaseOwnedAsync is { } tryRemoveIndex)
                        {
                            _ = await tryRemoveIndex(mutationContext.LeaseLostToken)
                                .ConfigureAwait(false);
                        }

                        throw;
                    }
                }
            }

            if (keyIndex is ITileCacheMutationCoordinator mutationCoordinator)
            {
                await mutationCoordinator
                    .ExecuteSerializedAsync(objectKey, UploadAndRecordAsync, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await UploadAndRecordAsync(new TileCacheMutationContext(
                    cancellationToken,
                    CancellationToken.None)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Tile cache writes are opportunistic; rendering has already succeeded, so a
            // storage-backend failure here must not fail the response. Record the
            // exception on the current span so the failure is still observable in
            // telemetry rather than silently disappearing.
            HonuaTelemetry.RecordException(Activity.Current, ex);
        }
    }

    private static async Task<GenerationObservation> ObserveGenerationAsync(
        ICloudFileStorage storage,
        ITileCacheKeyIndex? keyIndex,
        string objectKey,
        CancellationToken cancellationToken)
    {
        var expiredInIndex = keyIndex is { IsEnabled: true }
            && await keyIndex.IsExpiredAsync(objectKey, cancellationToken).ConfigureAwait(false);

        var current = await storage.GetMetadataAsync(objectKey, cancellationToken).ConfigureAwait(false);
        var fresh = current is not null
            && !expiredInIndex
            && (!current.ExpiresAt.HasValue || current.ExpiresAt.Value > DateTimeOffset.UtcNow);
        return new GenerationObservation(fresh, current is not null, current?.ETag);
    }

    internal static string BuildObjectKey(CloudStorageOptions? storageOptions, params string[] segments)
    {
        var normalized = new List<string>(segments.Length + 2);
        var prefix = ResolveProviderPrefix(storageOptions);
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            foreach (var segment in SplitPath(prefix))
            {
                normalized.Add(SanitizeSegment(segment));
            }
        }

        foreach (var segment in segments)
        {
            foreach (var part in SplitPath(segment))
            {
                normalized.Add(SanitizeSegment(part));
            }
        }

        return string.Join('/', normalized.Where(static segment => segment.Length > 0));
    }

    internal static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static TimeSpan ResolveTileCacheTtl(CloudStorageOptions? storageOptions)
    {
        var ttl = storageOptions?.DefaultTimeToLive;
        return ttl.HasValue && ttl.Value > TimeSpan.Zero
            ? ttl.Value
            : TimeSpan.FromHours(24);
    }

    private static string? ResolveProviderPrefix(CloudStorageOptions? storageOptions)
        => storageOptions?.Provider switch
        {
            CloudStorageProvider.AwsS3 => storageOptions.AwsS3?.KeyPrefix,
            CloudStorageProvider.AzureBlob => storageOptions.AzureBlob?.BlobPrefix,
            _ => null
        };

    private static string[] SplitPath(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string SanitizeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'
                ? ch
                : '_');
        }

        return builder.Length == 0
            ? "segment"
            : builder.ToString().ToLower(CultureInfo.InvariantCulture);
    }
}

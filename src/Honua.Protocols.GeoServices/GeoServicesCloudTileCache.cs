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
                await keyIndex.RecordAccessAsync(
                    objectKey,
                    data.LongLength,
                    metadata.ExpiresAt,
                    tenantScope,
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
                var ttl = ResolveTileCacheTtl(storageOptions);
                var expiresAt = DateTimeOffset.UtcNow.Add(ttl);
                using var stream = new MemoryStream(data, writable: false);
                var upload = await storage.UploadAsync(new FileUploadRequest
                {
                    Content = stream,
                    FileName = fileName,
                    ContentType = contentType,
                    SizeBytes = data.LongLength,
                    TimeToLive = ttl,
                    Metadata = metadata,
                    ObjectKeyOverride = objectKey,
                    EnableChunkedUpload = false
                }, mutationToken).ConfigureAwait(false);

                if (!upload.Success)
                {
                    return;
                }

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
                        await keyIndex.RecordWriteAsync(
                            objectKey,
                            data.LongLength,
                            recordedExpiresAt,
                            tenantScope,
                            mutationToken).ConfigureAwait(false);
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
                            storageRolledBack = await storage.DeleteAsync(
                                    objectKey,
                                    mutationContext.LeaseLostToken)
                                .ConfigureAwait(false);
                            if (!storageRolledBack)
                            {
                                storageRolledBack = await storage.GetMetadataAsync(
                                        objectKey,
                                        mutationContext.LeaseLostToken)
                                    .ConfigureAwait(false) is null;
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

                        if (storageRolledBack)
                        {
                            await keyIndex.RemoveAsync(
                                    objectKey,
                                    mutationContext.LeaseLostToken)
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

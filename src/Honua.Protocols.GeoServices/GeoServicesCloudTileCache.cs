// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

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
        CancellationToken cancellationToken)
    {
        if (storage is null || storageOptions?.Enabled == false)
        {
            return null;
        }

        try
        {
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

            var contentType = string.IsNullOrWhiteSpace(metadata.ContentType)
                ? DefaultContentType
                : metadata.ContentType;
            return new Hit(data, contentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
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
        CancellationToken cancellationToken)
    {
        if (storage is null || storageOptions?.Enabled == false || data.Length == 0)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            _ = await storage.UploadAsync(new FileUploadRequest
            {
                Content = stream,
                FileName = fileName,
                ContentType = contentType,
                SizeBytes = data.LongLength,
                TimeToLive = ResolveTileCacheTtl(storageOptions),
                Metadata = metadata,
                ObjectKeyOverride = objectKey,
                EnableChunkedUpload = false
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Tile cache writes are opportunistic; rendering has already succeeded.
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

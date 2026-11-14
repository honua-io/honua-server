// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.ServiceDefaults;

namespace Honua.Protocols.GeoServices;

internal static class GeoServicesCloudTileCache
{
    private const string DefaultContentType = "application/octet-stream";

    internal readonly record struct Hit(byte[] Data, string ContentType);

    private static readonly System.Diagnostics.Metrics.Counter<long> CacheHits =
        // `unit: null` on the instruments below is deliberate, not an oversight. The OpenTelemetry
        // Prometheus exporter derives the exported series name from the instrument name AND its unit:
        // it maps the unit through the UCUM table and appends it, so a declared unit renames the series
        // out from under every dashboard and alert rule without breaking a single build. A PromQL query
        // against the absent name returns an empty vector, so the panel is blank and the alert never
        // fires, silently. Units are documented in the instrument name and description instead. See the
        // SLO-contract comment block in HonuaTelemetry.cs, eng/telemetry/metric-name-contract.v1.json, and
        // MetricNameContractTests, which scrapes the real /metrics exposition and fails on drift.

        HonuaTelemetry.Meter.CreateCounter<long>(
            "honua.tile.cache.hits",
            unit: null,
            "Number of generated tile-cache serve-path hits.");

    private static readonly System.Diagnostics.Metrics.Counter<long> CacheMisses =
        HonuaTelemetry.Meter.CreateCounter<long>(
            "honua.tile.cache.misses",
            unit: null,
            "Number of generated tile-cache serve-path misses.");

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

        var hit = await TryReadCoreAsync(storage, objectKey, cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await storage.GetMetadataAsync(objectKey, cancellationToken).ConfigureAwait(false);
            if (metadata is null || IsExpired(metadata))
            {
                return null;
            }

            var data = await storage.DownloadBytesAsync(objectKey, cancellationToken).ConfigureAwait(false);
            if (data is not { Length: > 0 } || IsExpired(metadata))
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
        CancellationToken cancellationToken)
    {
        if (storage is null || storageOptions?.Enabled == false || data.Length == 0)
        {
            return;
        }

        try
        {
            var current = await storage.GetMetadataAsync(objectKey, cancellationToken).ConfigureAwait(false);
            if (current is not null && !IsExpired(current))
            {
                // Another request already committed this tile; keep that generation.
                return;
            }

            if (current is not null && string.IsNullOrWhiteSpace(current.ETag))
            {
                throw new InvalidOperationException(
                    $"The stale tile '{objectKey}' has no provider ETag for a generation-fenced replacement.");
            }

            var ttl = ResolveTileCacheTtl(storageOptions);
            using var stream = new MemoryStream(data, writable: false);
            _ = await storage.UploadIfMatchAsync(new FileUploadRequest
            {
                Content = stream,
                FileName = fileName,
                ContentType = contentType,
                SizeBytes = data.LongLength,
                TimeToLive = ttl,
                Metadata = metadata,
                ObjectKeyOverride = objectKey,
                EnableChunkedUpload = false
            }, current?.ETag, cancellationToken).ConfigureAwait(false);
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

    private static bool IsExpired(CloudFile metadata)
        => metadata.ExpiresAt.HasValue && metadata.ExpiresAt.Value <= DateTimeOffset.UtcNow;

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

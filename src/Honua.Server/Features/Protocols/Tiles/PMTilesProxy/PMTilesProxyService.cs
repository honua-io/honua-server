// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

/// <summary>
/// Resolves PMTiles range-proxy requests against the configured cloud storage
/// provider. Used by the public range-proxy endpoint to stream byte ranges from
/// private object storage without exposing signed URLs to browsers.
/// </summary>
internal sealed class PMTilesProxyService
{
    private const long MaxRangeLength = 64 * 1024 * 1024; // 64 MiB cap per range request

    private readonly ICloudFileStorage _cloudStorage;
    private readonly IEnumerable<ICloudRangeReader> _rangeReaders;
    private readonly CloudStorageOptions _storageOptions;

    public PMTilesProxyService(
        ICloudFileStorage cloudStorage,
        IEnumerable<ICloudRangeReader> rangeReaders,
        IOptions<CloudStorageOptions> storageOptions)
    {
        _cloudStorage = cloudStorage ?? throw new ArgumentNullException(nameof(cloudStorage));
        _rangeReaders = rangeReaders ?? throw new ArgumentNullException(nameof(rangeReaders));
        _storageOptions = storageOptions?.Value ?? throw new ArgumentNullException(nameof(storageOptions));
    }

    public async Task<PMTilesProxyResolution> ResolveAsync(string artifactId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artifactId))
        {
            return PMTilesProxyResolution.NotFound();
        }

        var metadata = await _cloudStorage.GetMetadataAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return PMTilesProxyResolution.NotFound();
        }

        return PMTilesProxyResolution.Found(metadata);
    }

    public async Task<PMTilesRangeResult> ReadRangeAsync(
        CloudFile metadata,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var totalSize = metadata.SizeBytes;
        if (string.IsNullOrWhiteSpace(rangeHeader))
        {
            return PMTilesRangeResult.Full(totalSize);
        }

        if (!TryParseSingleByteRange(rangeHeader, totalSize, out var start, out var end))
        {
            return PMTilesRangeResult.Unsatisfiable(totalSize);
        }

        var length = end - start + 1;
        if (length <= 0 || length > MaxRangeLength)
        {
            return PMTilesRangeResult.Unsatisfiable(totalSize);
        }

        var bucket = ResolveBucket(metadata.Provider);
        var reader = _rangeReaders.FirstOrDefault(r => r.Provider == metadata.Provider);
        byte[] payload;
        if (reader != null && !string.IsNullOrEmpty(bucket))
        {
            payload = await reader.ReadRangeAsync(bucket, metadata.StoragePath, start, (int)length, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            payload = await ReadRangeViaDownloadAsync(metadata, start, length, cancellationToken).ConfigureAwait(false);
        }

        return PMTilesRangeResult.Partial(start, end, totalSize, payload);
    }

    public async Task<Stream?> OpenFullAsync(string artifactId, CancellationToken cancellationToken)
    {
        return await _cloudStorage.DownloadAsync(artifactId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadRangeViaDownloadAsync(
        CloudFile metadata,
        long start,
        long length,
        CancellationToken cancellationToken)
    {
        await using var stream = await _cloudStorage.DownloadAsync(metadata.FileId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PMTiles artifact '{metadata.FileId}' could not be opened.");

        if (stream.CanSeek)
        {
            stream.Position = start;
        }
        else
        {
            // Discard up to start
            var skipBuffer = new byte[Math.Min(start, 81920)];
            var remainingToSkip = start;
            while (remainingToSkip > 0)
            {
                var requested = (int)Math.Min(skipBuffer.Length, remainingToSkip);
                var read = await stream.ReadAsync(skipBuffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                remainingToSkip -= read;
            }
        }

        var payload = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = await stream.ReadAsync(payload.AsMemory(totalRead, (int)(length - totalRead)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }

        if (totalRead < length)
        {
            Array.Resize(ref payload, totalRead);
        }

        return payload;
    }

    private string ResolveBucket(CloudStorageProvider provider) => provider switch
    {
        CloudStorageProvider.AwsS3 => _storageOptions.AwsS3?.BucketName ?? string.Empty,
        CloudStorageProvider.AzureBlob => _storageOptions.AzureBlob?.ContainerName ?? string.Empty,
        _ => string.Empty
    };

    internal static bool TryParseSingleByteRange(string headerValue, long totalSize, out long start, out long end)
    {
        start = 0;
        end = 0;

        if (!RangeHeaderValue.TryParse(headerValue, out var parsed) ||
            !string.Equals(parsed.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            parsed.Ranges.Count != 1)
        {
            return false;
        }

        var range = parsed.Ranges.First();
        if (range.From.HasValue)
        {
            start = range.From.Value;
            if (start >= totalSize)
            {
                return false;
            }

            end = range.To.HasValue
                ? Math.Min(range.To.Value, totalSize - 1)
                : totalSize - 1;
        }
        else if (range.To.HasValue)
        {
            // Suffix length: bytes=-N
            var suffix = range.To.Value;
            if (suffix == 0)
            {
                return false;
            }

            start = Math.Max(0, totalSize - suffix);
            end = totalSize - 1;
        }
        else
        {
            return false;
        }

        return start <= end && end < totalSize;
    }
}

internal readonly record struct PMTilesProxyResolution(bool Exists, CloudFile? Metadata)
{
    public static PMTilesProxyResolution NotFound() => new(false, null);
    public static PMTilesProxyResolution Found(CloudFile metadata) => new(true, metadata);
}

internal sealed record PMTilesRangeResult
{
    public required PMTilesRangeOutcome Outcome { get; init; }
    public required long TotalSize { get; init; }
    public long Start { get; init; }
    public long End { get; init; }
    public byte[]? Payload { get; init; }

    public static PMTilesRangeResult Full(long totalSize) => new()
    {
        Outcome = PMTilesRangeOutcome.Full,
        TotalSize = totalSize
    };

    public static PMTilesRangeResult Partial(long start, long end, long totalSize, byte[] payload) => new()
    {
        Outcome = PMTilesRangeOutcome.Partial,
        Start = start,
        End = end,
        TotalSize = totalSize,
        Payload = payload
    };

    public static PMTilesRangeResult Unsatisfiable(long totalSize) => new()
    {
        Outcome = PMTilesRangeOutcome.Unsatisfiable,
        TotalSize = totalSize
    };
}

internal enum PMTilesRangeOutcome
{
    Full,
    Partial,
    Unsatisfiable
}

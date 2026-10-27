// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Headers;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.FileStorage;

/// <summary>Bounded artifact byte acquisition pinned to the authorized metadata generation.</summary>
internal sealed class ArtifactByteReader
{
    private const long MaxRangeLength = 64 * 1024 * 1024; // 64 MiB cap per range request

    // A no-Range GET falls back to streaming the whole artifact. In a serverless
    // Lambda-proxy deployment the entire HTTP response has to fit inside a single
    // synchronous Lambda invocation result, which AWS hard-caps at 6 MB (larger still
    // once binary bodies are base64-encoded for the Lambda/API Gateway proxy envelope,
    // ~33% inflation). Above that ceiling the platform doesn't return a clean error: the
    // Lambda response fails to transmit and API Gateway/CloudFront synthesize an opaque
    // generic 500 with no correlation id (#2991) even though honua-server itself served
    // 200 internally. PMTiles browser clients always read archives via byte-range
    // requests (the header, directories, and tiles are all fetched by range) and never
    // perform a full-file GET in normal operation, so rejecting an oversized full-file
    // GET with a fast, bounded, honest error only affects callers that intentionally
    // want the whole artifact in one response (health checks, curl, wget) — never a
    // real map client — while keeping small archives' existing no-Range fast path
    // unchanged. Kept comfortably under the 6 MB Lambda ceiling even after the ~33%
    // base64 inflation a binary body incurs over that transport.
    internal const long MaxDirectFullStreamBytes = 4 * 1024 * 1024; // 4 MiB

    // Cap on how many bytes the download fallback may read-and-discard to reach
    // the range start when the provider stream is not seekable. Artifact clients
    // routinely request small ranges at large offsets (directories live at the
    // archive tail), so without this budget a provider with no ICloudRangeReader
    // would download an entire multi-GB archive from cloud storage to serve a few
    // KiB — a bandwidth amplification path on an unauthenticated endpoint. Ranges
    // beyond the budget are rejected as unsatisfiable instead.
    private const long MaxDownloadFallbackSkip = 8 * 1024 * 1024; // 8 MiB
    private readonly ICloudFileStorage _cloudStorage;
    private readonly IEnumerable<ICloudRangeReader> _rangeReaders;
    private readonly IReadOnlyList<ICloudNotFoundClassifier> _notFoundClassifiers;
    private readonly CloudStorageOptions _storageOptions;

    public ArtifactByteReader(
        ICloudFileStorage cloudStorage,
        IEnumerable<ICloudRangeReader> rangeReaders,
        IEnumerable<ICloudNotFoundClassifier> notFoundClassifiers,
        IOptions<CloudStorageOptions> storageOptions)
    {
        _cloudStorage = cloudStorage ?? throw new ArgumentNullException(nameof(cloudStorage));
        _rangeReaders = rangeReaders ?? throw new ArgumentNullException(nameof(rangeReaders));
        _notFoundClassifiers = (notFoundClassifiers ?? throw new ArgumentNullException(nameof(notFoundClassifiers))).ToArray();
        _storageOptions = storageOptions?.Value ?? throw new ArgumentNullException(nameof(storageOptions));
    }

    public async Task<ArtifactRangeResult> ReadRangeAsync(
        CloudFile metadata,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var totalSize = metadata.SizeBytes;
        if (string.IsNullOrWhiteSpace(rangeHeader))
        {
            return totalSize > MaxDirectFullStreamBytes
                ? ArtifactRangeResult.TooLarge(totalSize)
                : ArtifactRangeResult.Full(totalSize);
        }

        if (!TryParseSingleByteRange(rangeHeader, totalSize, out var start, out var end))
        {
            return ArtifactRangeResult.Unsatisfiable(totalSize);
        }

        var length = end - start + 1;
        if (length <= 0 || length > MaxRangeLength)
        {
            return ArtifactRangeResult.Unsatisfiable(totalSize);
        }

        // A Range response crosses the same synchronous serverless payload
        // boundary as a no-Range response. Reject callers that spell a full
        // oversized download as "bytes=0-" (and any other oversized range)
        // before reading the object; normal Artifact clients request small
        // header, directory, and tile slices.
        if (length > MaxDirectFullStreamBytes)
        {
            return ArtifactRangeResult.TooLarge(totalSize);
        }

        if (string.IsNullOrWhiteSpace(metadata.ETag))
        {
            return ArtifactRangeResult.Unsupported(totalSize);
        }

        byte[]? payload;
        try
        {
            var bucket = ResolveBucket(metadata.Provider);
            var reader = _rangeReaders.FirstOrDefault(r => r.Provider == metadata.Provider);
            if (reader != null && !string.IsNullOrEmpty(bucket))
            {
                try
                {
                    payload = await reader.ReadRangeAsync(bucket, metadata.StoragePath, start, (int)length, metadata.ETag, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsProviderNotFound(ex, _notFoundClassifiers))
                {
                    // S3 NoSuchKey / Azure 404. Match the download-fallback path
                    // by surfacing a missing underlying object as NotFound so the
                    // endpoint returns 404 instead of letting the global exception
                    // mapper turn the provider exception into a 500.
                    return ArtifactRangeResult.NotFound(totalSize);
                }
            }
            else
            {
                var fallback = await ReadRangeViaDownloadAsync(metadata, start, length, cancellationToken).ConfigureAwait(false);
                if (fallback.SkipBudgetExceeded)
                {
                    // No range reader is registered for this provider and the object
                    // stream cannot seek to the requested offset within the skip
                    // budget. Reject rather than downloading the whole object.
                    return ArtifactRangeResult.Unsatisfiable(totalSize);
                }

                payload = fallback.Payload;
            }

        }
        catch (CloudFileRevisionChangedException)
        {
            return ArtifactRangeResult.RevisionChanged(totalSize);
        }
        catch (NotSupportedException)
        {
            return ArtifactRangeResult.Unsupported(totalSize);
        }

        if (payload is null)
        {
            // Underlying object went missing between metadata lookup and range
            // read (out-of-band delete, lifecycle expiry, etc.). Surface this
            // as NotFound so the endpoint returns 404 — matching the no-Range
            // GET fallback that uses OpenFullAsync — instead of a 500.
            return ArtifactRangeResult.NotFound(totalSize);
        }

        if (payload.LongLength != length)
        {
            return ArtifactRangeResult.RevisionChanged(totalSize);
        }
        return ArtifactRangeResult.Partial(start, end, totalSize, payload);
    }

    /// <summary>
    /// Fans <paramref name="exception"/> through every registered
    /// <see cref="ICloudNotFoundClassifier"/> so the proxy can surface a missing
    /// underlying object as 404 without taking a direct dependency on AWS / Azure
    /// SDK exception types. Provider-specific classifiers live in Honua.Aws /
    /// Honua.Azure.
    /// </summary>
    internal static bool IsProviderNotFound(Exception exception, IReadOnlyList<ICloudNotFoundClassifier> classifiers)
    {
        for (var i = 0; i < classifiers.Count; i++)
        {
            if (classifiers[i].IsNotFound(exception))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<ArtifactRangeResult> ReadFullAsync(CloudFile metadata, CancellationToken cancellationToken)
    {
        if (metadata.SizeBytes > MaxDirectFullStreamBytes)
        {
            return ArtifactRangeResult.TooLarge(metadata.SizeBytes);
        }
        Stream? stream = null;
        var transferred = false;
        try
        {
            stream = await OpenFullAsync(metadata, cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                return ArtifactRangeResult.NotFound(metadata.SizeBytes);
            }
            if (stream.CanSeek && stream.Length != metadata.SizeBytes)
            {
                return ArtifactRangeResult.RevisionChanged(metadata.SizeBytes);
            }
            transferred = true;
            return ArtifactRangeResult.Full(metadata.SizeBytes) with { Stream = stream };
        }
        catch (CloudFileRevisionChangedException)
        {
            return ArtifactRangeResult.RevisionChanged(metadata.SizeBytes);
        }
        catch (NotSupportedException)
        {
            return ArtifactRangeResult.Unsupported(metadata.SizeBytes);
        }
        catch (Exception ex) when (IsProviderNotFound(ex, _notFoundClassifiers))
        {
            return ArtifactRangeResult.NotFound(metadata.SizeBytes);
        }
        finally
        {
            if (!transferred && stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<Stream?> OpenFullAsync(CloudFile metadata, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(metadata.ETag))
        {
            throw new NotSupportedException("The artifact has no storage generation validator.");
        }
        return await _cloudStorage.DownloadIfMatchAsync(metadata.FileId, metadata.ETag, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DownloadFallbackResult> ReadRangeViaDownloadAsync(
        CloudFile metadata,
        long start,
        long length,
        CancellationToken cancellationToken)
    {
        var stream = await OpenFullAsync(metadata, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return DownloadFallbackResult.Missing;
        }

        await using var _ = stream.ConfigureAwait(false);

        if (stream.CanSeek)
        {
            stream.Position = start;
        }
        else if (start > MaxDownloadFallbackSkip)
        {
            // The range starts beyond the skip budget on a non-seekable stream:
            // reaching it would download (and discard) the prefix of the whole
            // object. Bail out before pulling the object body.
            return DownloadFallbackResult.OverSkipBudget;
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

        return DownloadFallbackResult.Read(payload);
    }

    /// <summary>
    /// Result of the download-fallback range read: either the materialized payload,
    /// a missing underlying object (null payload), or a refusal because reaching the
    /// range start would exceed <see cref="MaxDownloadFallbackSkip"/>.
    /// </summary>
    private readonly record struct DownloadFallbackResult(byte[]? Payload, bool SkipBudgetExceeded)
    {
        public static DownloadFallbackResult Missing => new(null, false);
        public static DownloadFallbackResult OverSkipBudget => new(null, true);
        public static DownloadFallbackResult Read(byte[] payload) => new(payload, false);
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

internal sealed record ArtifactRangeResult
{
    public required ArtifactRangeOutcome Outcome { get; init; }
    public required long TotalSize { get; init; }
    public long Start { get; init; }
    public long End { get; init; }
    public byte[]? Payload { get; init; }
    public Stream? Stream { get; init; }

    public static ArtifactRangeResult Full(long totalSize) => new()
    {
        Outcome = ArtifactRangeOutcome.Full,
        TotalSize = totalSize
    };

    public static ArtifactRangeResult Partial(long start, long end, long totalSize, byte[] payload) => new()
    {
        Outcome = ArtifactRangeOutcome.Partial,
        Start = start,
        End = end,
        TotalSize = totalSize,
        Payload = payload
    };

    public static ArtifactRangeResult Unsatisfiable(long totalSize) => new()
    {
        Outcome = ArtifactRangeOutcome.Unsatisfiable,
        TotalSize = totalSize
    };

    public static ArtifactRangeResult NotFound(long totalSize) => new()
    {
        Outcome = ArtifactRangeOutcome.NotFound,
        TotalSize = totalSize
    };

    public static ArtifactRangeResult RevisionChanged(long totalSize) => new()
    {
        Outcome = ArtifactRangeOutcome.RevisionChanged,
        TotalSize = totalSize
    };

    public static ArtifactRangeResult Unsupported(long totalSize) => new()
    {
        Outcome = ArtifactRangeOutcome.Unsupported,
        TotalSize = totalSize
    };

    public static ArtifactRangeResult TooLarge(long totalSize) => new()
    {
        Outcome = ArtifactRangeOutcome.TooLarge,
        TotalSize = totalSize
    };
}

internal enum ArtifactRangeOutcome
{
    Full,
    Partial,
    Unsatisfiable,
    NotFound,
    TooLarge,
    RevisionChanged,
    Unsupported
}

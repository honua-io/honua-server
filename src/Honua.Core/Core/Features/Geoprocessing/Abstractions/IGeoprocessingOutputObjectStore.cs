// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public enum GeoprocessingRetentionHoldResult
{
    ObjectMissing,

    AlreadyHeld,

    Added
}

public interface IGeoprocessingOutputObjectStore
{
    CloudStorageProvider Provider { get; }

    string StoreReference { get; }

    Task<RasterContentIdentity> WriteAsync(
        string objectKey,
        Stream content,
        string mediaType,
        CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default);

    Task<GeoprocessingStagedObjectInfo?> GetInfoAsync(string objectKey, CancellationToken cancellationToken = default);

    IAsyncEnumerable<GeoprocessingStagedObjectInfo> ListAsync(
        string keyPrefix,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken = default);

    Task<bool> TryAcquireReadLeaseAsync(
        string objectKey,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    Task<bool> HasActiveReadLeaseAsync(string objectKey, CancellationToken cancellationToken = default);

    Task<GeoprocessingRetentionHoldResult> SetRetentionHoldAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    Task ReleaseRetentionHoldAsync(string objectKey, CancellationToken cancellationToken = default);

    Task<bool> HasRetentionHoldAsync(string objectKey, CancellationToken cancellationToken = default);
}

public sealed record GeoprocessingStagedObjectInfo(
    string ObjectKey,
    long SizeBytes,
    DateTimeOffset LastModifiedAt);

public static class GeoprocessingOutputObjectKeys
{
    public static string Build(string keyPrefix, string jobId, int attemptNumber, string outputName, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(attemptNumber, 0);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        return $"{keyPrefix.TrimEnd('/')}/{jobId}/a{attemptNumber}/{outputName}/{fileName}";
    }

    public static bool TryParse(
        string keyPrefix,
        string objectKey,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? jobId,
        out int attemptNumber)
    {
        jobId = null;
        attemptNumber = 0;
        if (string.IsNullOrWhiteSpace(keyPrefix) || string.IsNullOrWhiteSpace(objectKey))
        {
            return false;
        }

        var prefix = keyPrefix.TrimEnd('/') + "/";
        if (!objectKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = objectKey.AsSpan(prefix.Length);
        var segments = remainder.ToString().Split('/', StringSplitOptions.None);
        if (segments.Length < 4)
        {
            return false;
        }

        var attemptSegment = segments[1];
        if (attemptSegment.Length < 2
            || attemptSegment[0] != 'a'
            || !int.TryParse(attemptSegment.AsSpan(1), out var attempt)
            || attempt <= 0
            || string.IsNullOrWhiteSpace(segments[0]))
        {
            return false;
        }

        jobId = segments[0];
        attemptNumber = attempt;
        return true;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>Result of atomically establishing a durable retention hold.</summary>
public enum GeoprocessingRetentionHoldResult
{
    /// <summary>The object no longer exists, so no hold was established.</summary>
    ObjectMissing,

    /// <summary>A durable hold already existed.</summary>
    AlreadyHeld,

    /// <summary>This call established the durable hold.</summary>
    Added
}

/// <summary>
/// Deterministic-key object store for staged geoprocessing output artifacts (#3089).
/// </summary>
/// <remarks>
/// Workers stream large job outputs into attempt-scoped immutable keys built by
/// <see cref="GeoprocessingOutputObjectKeys"/> and publish typed
/// <see cref="StagedObjectRasterOutputDescriptor"/> references instead of payload
/// bytes. Server-side readers (result download, registration, the orphan sweeper)
/// resolve the same keys. Implementations expose no presigned or expiring URLs and
/// never surface provider credentials through this contract.
/// </remarks>
public interface IGeoprocessingOutputObjectStore
{
    /// <summary>Storage provider backing this store.</summary>
    CloudStorageProvider Provider { get; }

    /// <summary>
    /// Logical operator-registered store identity recorded on descriptors. It is not
    /// a provider connection string, URI, or credential.
    /// </summary>
    string StoreReference { get; }

    /// <summary>
    /// Streams content into a new immutable object at <paramref name="objectKey"/>,
    /// computing size and a sha256 checksum while copying. Fails when the key already
    /// exists: attempt-scoped keys are written at most once per attempt, so an existing
    /// object indicates a duplicate write rather than a retry.
    /// </summary>
    /// <param name="objectKey">Attempt-scoped object key.</param>
    /// <param name="content">Content stream; read once, never buffered whole.</param>
    /// <param name="mediaType">IANA media type recorded with the object.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The written object's content identity.</returns>
    Task<RasterContentIdentity> WriteAsync(
        string objectKey,
        Stream content,
        string mediaType,
        CancellationToken cancellationToken = default);

    /// <summary>Opens the object for streaming read, or null when it does not exist.</summary>
    /// <param name="objectKey">Object key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A readable stream positioned at the start, or null.</returns>
    Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>Returns object metadata, or null when the object does not exist.</summary>
    /// <param name="objectKey">Object key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Object info, or null.</returns>
    Task<GeoprocessingStagedObjectInfo?> GetInfoAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>Lists staged objects under a key prefix (lease sidecars excluded).</summary>
    /// <param name="keyPrefix">Key prefix to enumerate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Staged object descriptions.</returns>
    IAsyncEnumerable<GeoprocessingStagedObjectInfo> ListAsync(
        string keyPrefix,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an object; returns false when it did not exist.</summary>
    /// <param name="objectKey">Object key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether an object was deleted.</returns>
    Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires or refreshes a bounded read lease on the object so the orphan sweeper
    /// will not delete it while a caller is streaming it. Returns false when the object
    /// does not exist.
    /// </summary>
    /// <param name="objectKey">Object key.</param>
    /// <param name="duration">Lease duration from now.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the lease was acquired.</returns>
    Task<bool> TryAcquireReadLeaseAsync(
        string objectKey,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    /// <summary>Whether an unexpired read lease exists for the object.</summary>
    /// <param name="objectKey">Object key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether a live read lease exists.</returns>
    Task<bool> HasActiveReadLeaseAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Places a durable retention hold on the object. Held objects are permanently
    /// exempt from orphan sweeping: registration into a long-lived catalog (for
    /// example <c>cloud_raster_catalog</c>) outlives the expiring job record, so the
    /// hold — not the job record — is what keeps the registered object alive.
    /// Idempotent. Returns <see langword="false"/> when the object does not exist.
    /// </summary>
    /// <param name="objectKey">Object key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether this call added the hold, found one, or found no object.</returns>
    Task<GeoprocessingRetentionHoldResult> SetRetentionHoldAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a durable retention hold. Used only to compensate a newly established hold after
    /// the corresponding permanent catalog write definitively failed. Idempotent.
    /// </summary>
    /// <param name="objectKey">Object key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReleaseRetentionHoldAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>Whether a durable retention hold exists for the object.</summary>
    /// <param name="objectKey">Object key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the object is held.</returns>
    Task<bool> HasRetentionHoldAsync(string objectKey, CancellationToken cancellationToken = default);
}

/// <summary>Metadata for a staged geoprocessing output object.</summary>
/// <param name="ObjectKey">Object key within the store.</param>
/// <param name="SizeBytes">Object size in bytes.</param>
/// <param name="LastModifiedAt">Last write time (UTC).</param>
public sealed record GeoprocessingStagedObjectInfo(
    string ObjectKey,
    long SizeBytes,
    DateTimeOffset LastModifiedAt);

/// <summary>
/// Canonical attempt-scoped key scheme for staged geoprocessing outputs:
/// <c>{prefix}/{jobId}/a{attempt}/{outputName}/{fileName}</c>. Attempt scoping keeps
/// keys immutable across retries — a retried attempt writes new keys and can never
/// overwrite the objects a previous attempt staged.
/// </summary>
public static class GeoprocessingOutputObjectKeys
{
    /// <summary>Builds the attempt-scoped object key for one logical output.</summary>
    /// <param name="keyPrefix">Configured store key prefix.</param>
    /// <param name="jobId">Durable job identifier.</param>
    /// <param name="attemptNumber">Producing attempt number (positive).</param>
    /// <param name="outputName">Logical output name.</param>
    /// <param name="fileName">Terminal file name including extension.</param>
    /// <returns>The immutable object key.</returns>
    public static string Build(string keyPrefix, string jobId, int attemptNumber, string outputName, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(attemptNumber, 0);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        return $"{keyPrefix.TrimEnd('/')}/{jobId}/a{attemptNumber}/{outputName}/{fileName}";
    }

    /// <summary>
    /// Parses a staged object key back to its job and attempt identity. Returns false
    /// for keys outside the canonical scheme so the sweeper never touches foreign objects.
    /// </summary>
    /// <param name="keyPrefix">Configured store key prefix.</param>
    /// <param name="objectKey">Candidate object key.</param>
    /// <param name="jobId">Parsed job identifier.</param>
    /// <param name="attemptNumber">Parsed attempt number.</param>
    /// <returns>Whether the key matches the canonical scheme.</returns>
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

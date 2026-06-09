// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Caching;

/// <summary>
/// Outcome of a metadata cache lookup or refresh.
/// </summary>
public enum MetadataCacheStatus
{
    /// <summary>A fresh cached entry was served.</summary>
    Hit,

    /// <summary>No cached entry was available.</summary>
    Miss,

    /// <summary>A stale cached entry was served within the stale-if-error window.</summary>
    Stale,

    /// <summary>The entry was refreshed from the source.</summary>
    Refreshed,

    /// <summary>The cache was bypassed for this lookup.</summary>
    Bypass,

    /// <summary>The cache lookup or refresh failed.</summary>
    Failed
}

/// <summary>
/// HTTP validators retained with metadata cache entries.
/// </summary>
public sealed record MetadataCacheValidators
{
    /// <summary>
    /// Empty validator set.
    /// </summary>
    public static readonly MetadataCacheValidators None = new();

    /// <summary>
    /// Entity tag returned by the source metadata endpoint.
    /// </summary>
    public string? ETag { get; init; }

    /// <summary>
    /// Last-Modified timestamp returned by the source metadata endpoint.
    /// </summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// Whether at least one validator is available for conditional revalidation.
    /// </summary>
    public bool HasValidators => !string.IsNullOrWhiteSpace(ETag) || LastModified.HasValue;
}

/// <summary>
/// Observable state associated with a metadata cache lookup, entry, or refresh attempt.
/// </summary>
public sealed record MetadataCacheState
{
    /// <summary>
    /// Cache lookup or refresh status.
    /// </summary>
    public required MetadataCacheStatus Status { get; init; }

    /// <summary>
    /// Fingerprint of the full cache key. The raw key can stay out of logs and traces.
    /// </summary>
    public required string KeyFingerprint { get; init; }

    /// <summary>
    /// Age of the cached metadata when observed.
    /// </summary>
    public TimeSpan? Age { get; init; }

    /// <summary>
    /// Configured freshness lifetime for the entry.
    /// </summary>
    public TimeSpan? Ttl { get; init; }

    /// <summary>
    /// Configured stale-if-error window for the entry.
    /// </summary>
    public TimeSpan? StaleIfError { get; init; }

    /// <summary>
    /// HTTP validators available for conditional revalidation.
    /// </summary>
    public MetadataCacheValidators Validators { get; init; } = MetadataCacheValidators.None;

    /// <summary>
    /// Earliest time at which the entry should be revalidated.
    /// </summary>
    public DateTimeOffset? RevalidateAfter { get; init; }

    /// <summary>
    /// Reason an entry was invalidated or bypassed.
    /// </summary>
    public string? InvalidationReason { get; init; }

    /// <summary>
    /// Stable error identifier associated with a failed refresh.
    /// </summary>
    public string? RefreshErrorId { get; init; }

    /// <summary>
    /// Whether the observed state represents a usable cached metadata value.
    /// </summary>
    public bool HasUsableEntry => Status is MetadataCacheStatus.Hit or MetadataCacheStatus.Stale or MetadataCacheStatus.Refreshed;
}

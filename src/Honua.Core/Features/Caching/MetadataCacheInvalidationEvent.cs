// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Caching;

/// <summary>
/// Stable reason for invalidating metadata cache entries.
/// </summary>
public enum MetadataCacheInvalidationReason
{
    Manual,
    TenantChanged,
    ProjectChanged,
    SourceChanged,
    SchemaChanged,
    StyleChanged,
    PermissionChanged,
    AdapterVersionChanged,
    ProjectionVersionChanged,
    SourceUnavailable
}

/// <summary>
/// Targeted metadata cache invalidation event.
/// </summary>
public sealed record MetadataCacheInvalidationEvent
{
    /// <summary>
    /// Tenant scope to invalidate. Null or whitespace matches all tenants.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Project scope to invalidate. Null or whitespace matches all projects.
    /// </summary>
    public string? ProjectId { get; init; }

    /// <summary>
    /// Source endpoint fingerprint to invalidate. Null or whitespace matches all sources.
    /// </summary>
    public string? SourceFingerprint { get; init; }

    /// <summary>
    /// Adapter implementation to invalidate. Null or whitespace matches all adapters.
    /// </summary>
    public string? Adapter { get; init; }

    /// <summary>
    /// Resource identifier to invalidate. Null or whitespace matches all resources.
    /// </summary>
    public string? ResourceId { get; init; }

    /// <summary>
    /// Reason for the invalidation.
    /// </summary>
    public required MetadataCacheInvalidationReason Reason { get; init; }

    /// <summary>
    /// Time the invalidation was emitted.
    /// </summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Returns true when this invalidation event targets the supplied metadata cache request.
    /// Empty target properties are treated as wildcards.
    /// </summary>
    public bool Matches(MetadataCacheKeyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return MatchesScopedComponent(TenantId, request.TenantId)
            && MatchesScopedComponent(ProjectId, request.ProjectId)
            && MatchesSource(request.SourceUrl)
            && MatchesComponent(Adapter, request.Adapter, "unknown")
            && MatchesScopedComponent(ResourceId, request.ResourceId);
    }

    private bool MatchesSource(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(SourceFingerprint))
        {
            return true;
        }

        return string.Equals(
            SourceFingerprint,
            MetadataCacheKeyBuilder.FingerprintSource(sourceUrl),
            StringComparison.Ordinal);
    }

    private static bool MatchesComponent(string? target, string? candidate, string fallback)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return true;
        }

        return string.Equals(
            MetadataCacheKeyBuilder.NormalizeForMatch(target, fallback),
            MetadataCacheKeyBuilder.NormalizeForMatch(candidate, fallback),
            StringComparison.Ordinal);
    }

    private static bool MatchesScopedComponent(string? target, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return true;
        }

        return string.Equals(
            MetadataCacheKeyBuilder.NormalizeScopedForMatch(target),
            MetadataCacheKeyBuilder.NormalizeScopedForMatch(candidate),
            StringComparison.Ordinal);
    }
}

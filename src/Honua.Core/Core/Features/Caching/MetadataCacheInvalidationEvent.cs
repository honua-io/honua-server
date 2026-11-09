// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Caching;

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

public sealed record MetadataCacheInvalidationEvent
{
    public string? TenantId { get; init; }

    public string? ProjectId { get; init; }

    public string? SourceFingerprint { get; init; }

    public string? Adapter { get; init; }

    public string? ResourceId { get; init; }

    public required MetadataCacheInvalidationReason Reason { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

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

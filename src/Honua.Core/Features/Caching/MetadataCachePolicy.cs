// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Caching;

/// <summary>
/// Metadata and response classes known to the platform cache policy catalog.
/// </summary>
public enum MetadataCacheContentClass
{
    ServiceList,
    LayerDescriptor,
    Fields,
    Domains,
    Capabilities,
    Relationships,
    RenderersStyles,
    Legends,
    TileMatrixSets,
    StacCollectionMetadata,
    OgcProcessDescriptions,
    FeatureResponse,
    QueryResponse,
    ResultResponse,
    RealtimeResponse,
    RouteResponse,
    ProcessResultResponse
}

/// <summary>
/// TTL and stale-if-error policy for a metadata cache content class.
/// </summary>
public sealed record MetadataCachePolicy
{
    /// <summary>
    /// Content class governed by the policy.
    /// </summary>
    public required MetadataCacheContentClass ContentClass { get; init; }

    /// <summary>
    /// Whether this content class should be stored in the metadata cache.
    /// </summary>
    public required bool IsCacheable { get; init; }

    /// <summary>
    /// Freshness lifetime for cacheable content.
    /// </summary>
    public required TimeSpan Ttl { get; init; }

    /// <summary>
    /// Additional window where stale metadata can be served if source refresh fails.
    /// </summary>
    public required TimeSpan StaleIfError { get; init; }

    /// <summary>
    /// Reason the content class bypasses metadata caching.
    /// </summary>
    public string? NoCacheReason { get; init; }

    /// <summary>
    /// Returns the default policy for a known metadata or no-cache content class.
    /// </summary>
    public static MetadataCachePolicy For(MetadataCacheContentClass contentClass)
    {
        return contentClass switch
        {
            MetadataCacheContentClass.ServiceList => Cacheable(contentClass, TimeSpan.FromHours(1), TimeSpan.FromHours(6)),
            MetadataCacheContentClass.LayerDescriptor => Cacheable(contentClass, TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)),
            MetadataCacheContentClass.Fields => Cacheable(contentClass, TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)),
            MetadataCacheContentClass.Domains => Cacheable(contentClass, TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)),
            MetadataCacheContentClass.Capabilities => Cacheable(contentClass, TimeSpan.FromHours(1), TimeSpan.FromHours(6)),
            MetadataCacheContentClass.Relationships => Cacheable(contentClass, TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)),
            MetadataCacheContentClass.RenderersStyles => Cacheable(contentClass, TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)),
            MetadataCacheContentClass.Legends => Cacheable(contentClass, TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)),
            MetadataCacheContentClass.TileMatrixSets => Cacheable(contentClass, TimeSpan.FromHours(24), TimeSpan.FromDays(7)),
            MetadataCacheContentClass.StacCollectionMetadata => Cacheable(contentClass, TimeSpan.FromHours(1), TimeSpan.FromHours(6)),
            MetadataCacheContentClass.OgcProcessDescriptions => Cacheable(contentClass, TimeSpan.FromHours(1), TimeSpan.FromHours(6)),
            MetadataCacheContentClass.FeatureResponse => NoCache(contentClass, "ad hoc feature responses are high-cardinality and caller-specific"),
            MetadataCacheContentClass.QueryResponse => NoCache(contentClass, "ad hoc query responses are high-cardinality and caller-specific"),
            MetadataCacheContentClass.ResultResponse => NoCache(contentClass, "result responses are execution outputs, not reusable metadata"),
            MetadataCacheContentClass.RealtimeResponse => NoCache(contentClass, "realtime responses must not be replayed from metadata cache"),
            MetadataCacheContentClass.RouteResponse => NoCache(contentClass, "route responses depend on ad hoc origin, destination, and network state"),
            MetadataCacheContentClass.ProcessResultResponse => NoCache(contentClass, "process result responses are job outputs, not metadata"),
            _ => throw new ArgumentOutOfRangeException(nameof(contentClass), contentClass, "Unknown metadata cache content class.")
        };
    }

    /// <summary>
    /// Returns whether the supplied content class is allowed to use the metadata cache.
    /// </summary>
    public static bool IsCacheableContent(MetadataCacheContentClass contentClass) => For(contentClass).IsCacheable;

    private static MetadataCachePolicy Cacheable(
        MetadataCacheContentClass contentClass,
        TimeSpan ttl,
        TimeSpan staleIfError)
    {
        return new MetadataCachePolicy
        {
            ContentClass = contentClass,
            IsCacheable = true,
            Ttl = ttl,
            StaleIfError = staleIfError
        };
    }

    private static MetadataCachePolicy NoCache(MetadataCacheContentClass contentClass, string reason)
    {
        return new MetadataCachePolicy
        {
            ContentClass = contentClass,
            IsCacheable = false,
            Ttl = TimeSpan.Zero,
            StaleIfError = TimeSpan.Zero,
            NoCacheReason = reason
        };
    }
}

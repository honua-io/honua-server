// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Caching;

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

public sealed record MetadataCachePolicy
{
    public required MetadataCacheContentClass ContentClass { get; init; }

    public required bool IsCacheable { get; init; }

    public required TimeSpan Ttl { get; init; }

    public required TimeSpan StaleIfError { get; init; }

    public string? NoCacheReason { get; init; }

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

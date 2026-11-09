// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Caching;

public interface IPreparedStatementCacheStatisticsProvider
{
    PreparedStatementCacheStatistics GetStatistics();
}

public sealed record PreparedStatementCacheStatistics
{
    public int TotalStatements { get; init; }

    public int CacheHits { get; init; }

    public int CacheMisses { get; init; }

    public int PreparedStatements { get; init; }

    public double HitRatio => CacheHits + CacheMisses > 0
        ? (double)CacheHits / (CacheHits + CacheMisses)
        : 0;
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

public sealed class QueryExecutionContext
{
    public string CorrelationId { get; set; } = string.Empty;

    public string QueryType { get; set; } = string.Empty;

    public int? LayerId { get; set; }

    public string? UserId { get; set; }

    public Dictionary<string, object>? Metadata { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}

public sealed record QueryCacheStatistics
{
    public long TotalRequests { get; init; }

    public long CacheHits { get; init; }

    public long CacheMisses { get; init; }

    public double HitRatio { get; init; }

    public long TotalTimeSavedMs { get; init; }

    public double AvgMissExecutionTimeMs { get; init; }

    public int CurrentCacheSize { get; init; }

    public long TotalCacheMemoryBytes { get; init; }

    public IReadOnlyDictionary<string, QueryTypeCacheStatistics> ByQueryType { get; init; } =
        new Dictionary<string, QueryTypeCacheStatistics>();

    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
}

public sealed record QueryTypeCacheStatistics
{
    public long Requests { get; init; }

    public long Hits { get; init; }

    public double AvgExecutionTimeMs { get; init; }
}

public sealed record CacheEffectivenessMetrics
{
    public IReadOnlyList<CacheKeyMetrics> TopAccessedKeys { get; init; } =
        Array.Empty<CacheKeyMetrics>();

    public IReadOnlyList<CacheKeyMetrics> LowEfficiencyKeys { get; init; } =
        Array.Empty<CacheKeyMetrics>();

    public IReadOnlyList<CacheKeyMetrics> LargestItems { get; init; } =
        Array.Empty<CacheKeyMetrics>();

    public CacheEvictionMetrics EvictionMetrics { get; init; } = new();
}

public sealed record CacheKeyMetrics
{
    public string Key { get; init; } = string.Empty;

    public long AccessCount { get; init; }

    public double HitRatio { get; init; }

    public long MemoryBytes { get; init; }

    public DateTime LastAccessed { get; init; }

    public double AvgExecutionTimeMs { get; init; }
}

public sealed record CacheEvictionMetrics
{
    public long TotalEvictions { get; init; }

    public long MemoryPressureEvictions { get; init; }

    public long ExpirationEvictions { get; init; }

    public long ManualEvictions { get; init; }
}

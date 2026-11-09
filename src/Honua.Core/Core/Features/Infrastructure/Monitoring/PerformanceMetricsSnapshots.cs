// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

public interface IDatabasePerformanceMetricsProvider
{
    DatabasePerformanceMetricsSnapshot GetMetrics();
}

public interface ICacheMetricsSnapshotProvider
{
    CacheMetricsSnapshot GetCacheMetricsSnapshot();
}

public interface IHttpRequestMetricsSnapshotProvider
{
    HttpRequestMetricsSnapshot GetHttpRequestMetricsSnapshot();
}

public sealed record DatabasePerformanceMetricsSnapshot
{
    public double CacheHitRate { get; init; }

    public long CacheHits { get; init; }

    public long CacheMisses { get; init; }

    public Dictionary<string, DatabaseOperationMetricsSnapshot> Operations { get; init; } = new();
}

public sealed record DatabaseOperationMetricsSnapshot
{
    public long Count { get; init; }

    public long TotalTimeMs { get; init; }

    public long MaxTimeMs { get; init; }

    public double AvgTimeMs { get; init; }
}

public sealed record CacheMetricsSnapshot
{
    public long TotalHits { get; init; }

    public long TotalMisses { get; init; }

    public long TotalEvictions { get; init; }

    public Dictionary<string, CacheTypeMetricsSnapshot> Types { get; init; } = new();
}

public sealed record CacheTypeMetricsSnapshot
{
    public long Hits { get; init; }

    public long Misses { get; init; }

    public long Evictions { get; init; }
}

public sealed record HttpRequestMetricsSnapshot
{
    public long TotalRequests { get; init; }

    public long TotalServerErrors { get; init; }

    public long TotalClientErrors { get; init; }

    public int ActiveRequests { get; init; }

    public double AvgDurationMs { get; init; }

    public double MaxDurationMs { get; init; }

    public double P95DurationMs { get; init; }

    public long SlowRequestCount { get; init; }

    public double SlowRequestThresholdMs { get; init; }
}

internal sealed class NullDatabasePerformanceMetricsProvider : IDatabasePerformanceMetricsProvider
{
    public DatabasePerformanceMetricsSnapshot GetMetrics() => new();
}

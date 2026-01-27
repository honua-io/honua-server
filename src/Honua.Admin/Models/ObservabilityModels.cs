// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Models;

public sealed class HealthMetrics
{
    public string? Status { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public double MemoryUsageMB { get; set; }
    public double MemoryPressurePercent { get; set; }
    public int GCCollections { get; set; }
}

public sealed class PerformanceMetricsResponse
{
    public DateTimeOffset Timestamp { get; set; }
    public MemoryUsage? Memory { get; set; }
    public SystemInfo? SystemInfo { get; set; }
    public HttpRequestMetrics? Http { get; set; }
}

public sealed class SystemInfo
{
    public int ProcessorCount { get; set; }
    public string? MachineName { get; set; }
    public long WorkingSet { get; set; }
    public string? FrameworkVersion { get; set; }
}

public sealed class HttpRequestMetrics
{
    public long TotalRequests { get; set; }
    public long TotalServerErrors { get; set; }
    public long TotalClientErrors { get; set; }
    public int ActiveRequests { get; set; }
    public double AvgDurationMs { get; set; }
    public double MaxDurationMs { get; set; }
    public double P95DurationMs { get; set; }
    public long SlowRequests { get; set; }
    public double SlowRequestThresholdMs { get; set; }
    public double ServerErrorRate { get; set; }
}

public sealed class DatabaseMetrics
{
    public DateTimeOffset Timestamp { get; set; }
    public double CacheHitRate { get; set; }
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public Dictionary<string, DatabaseOperationMetrics>? Operations { get; set; }
}

public sealed class DatabaseOperationMetrics
{
    public long Count { get; set; }
    public long TotalTimeMs { get; set; }
    public long MaxTimeMs { get; set; }
    public double AvgTimeMs { get; set; }
}

public sealed class CacheMetrics
{
    public DateTimeOffset Timestamp { get; set; }
    public long TotalRequests { get; set; }
    public double HitRatio { get; set; }
    public Dictionary<string, CacheTypeMetrics>? Types { get; set; }
}

public sealed class CacheTypeMetrics
{
    public long Hits { get; set; }
    public long Misses { get; set; }
    public long Evictions { get; set; }
    public double AvgOperationTimeMs { get; set; }
    public double HitRatio { get; set; }
}

public sealed class MemoryUsage
{
    public long AllocatedBytes { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public long HeapSizeBytes { get; set; }
    public long HighMemoryLoadThresholdBytes { get; set; }
    public long MemoryLoadBytes { get; set; }
    public long TotalAvailableMemoryBytes { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public double MemoryPressurePercentage { get; set; }
    public bool IsHighMemoryPressure { get; set; }
    public int TotalGCCollections { get; set; }
}

public sealed class RecentErrorEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public string? CorrelationId { get; set; }
    public string? Path { get; set; }
    public int StatusCode { get; set; }
    public string? Message { get; set; }
}

public sealed class RecentErrorsResponse
{
    public int Capacity { get; set; }
    public List<RecentErrorEntry>? Errors { get; set; }
}

public sealed class ObservabilityStatusResponse
{
    public bool TracingEnabled { get; set; }
    public bool OtlpConfigured { get; set; }
    public string? OtlpEndpoint { get; set; }
}

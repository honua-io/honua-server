// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Infrastructure.Monitoring;

public sealed class HealthMetrics
{
    public required string Status { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required double MemoryUsageMB { get; init; }

    public required double MemoryPressurePercent { get; init; }

    public required int GCCollections { get; init; }

    public required MigrationHealthMetrics Migration { get; init; }
}

public sealed class MigrationHealthMetrics
{
    public required string Status { get; init; }

    public required bool IsReady { get; init; }

    public required bool IsFailed { get; init; }

    public string? Message { get; init; }
}

public sealed class PerformanceMetricsResponse
{
    public required DateTimeOffset Timestamp { get; init; }

    public required MemoryUsage Memory { get; init; }

    public required SystemInfo SystemInfo { get; init; }

    public required HttpRequestMetrics Http { get; init; }
}

public sealed class SystemInfo
{
    public required int ProcessorCount { get; init; }

    public required string MachineName { get; init; }

    public required long WorkingSet { get; init; }

    public required string FrameworkVersion { get; init; }
}

public sealed class DatabaseMetrics
{
    public required DateTimeOffset Timestamp { get; init; }

    public required double CacheHitRate { get; init; }

    public required long CacheHits { get; init; }

    public required long CacheMisses { get; init; }

    public required Dictionary<string, DatabaseOperationMetrics> Operations { get; init; }
}

public sealed class HttpRequestMetrics
{
    public required long TotalRequests { get; init; }

    public required long TotalServerErrors { get; init; }

    public required long TotalClientErrors { get; init; }

    public required int ActiveRequests { get; init; }

    public required double AvgDurationMs { get; init; }

    public required double MaxDurationMs { get; init; }

    public required double P95DurationMs { get; init; }

    public required long SlowRequests { get; init; }

    public required double SlowRequestThresholdMs { get; init; }

    public required double ServerErrorRate { get; init; }
}

public sealed class DatabaseOperationMetrics
{
    public required long Count { get; init; }

    public required long TotalTimeMs { get; init; }

    public required long MaxTimeMs { get; init; }

    public required double AvgTimeMs { get; init; }
}

public sealed class CacheMetrics
{
    public required DateTimeOffset Timestamp { get; init; }

    public required long TotalRequests { get; init; }

    public required double HitRatio { get; init; }

    public required Dictionary<string, CacheTypeMetrics> Types { get; init; }
}

public sealed class StreamingMetrics
{
    public required DateTimeOffset Timestamp { get; init; }

    public required int ActiveConnections { get; init; }

    public required int WebSocketConnections { get; init; }

    public required int SseConnections { get; init; }

    public required long SlowConsumerDrops { get; init; }

    public required long HeartbeatsSent { get; init; }
}

public sealed class CacheTypeMetrics
{
    public required long Hits { get; init; }

    public required long Misses { get; init; }

    public double HitRatio => Hits + Misses > 0 ? (double)Hits / (Hits + Misses) : 0.0;

    public required long Evictions { get; init; }

}

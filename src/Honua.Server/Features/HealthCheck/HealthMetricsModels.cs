// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.HealthCheck;

internal sealed class HealthPerformanceMetricsResponse
{
    public required DateTimeOffset Timestamp { get; init; }

    public required string Status { get; init; }

    public required double PerformanceScore { get; init; }

    public required HealthPerformanceMetrics Metrics { get; init; }
}

internal sealed class HealthPerformanceMetrics
{
    public required DatabasePerformanceMetricsSnapshot QueryPerformance { get; init; }

    public required HealthMemoryMetrics Memory { get; init; }

    public required HealthGcMetrics GcInfo { get; init; }

    public HealthCacheRefreshMetrics? CacheRefresh { get; init; }
}

internal sealed class HealthCacheRefreshMetrics
{
    public required int QueueDepth { get; init; }

    public required long SuccessCount { get; init; }

    public required long FailureCount { get; init; }

    public required long SkippedCount { get; init; }
}

internal sealed class HealthMemoryMetrics
{
    public required long TotalBytes { get; init; }

    public required long HeapSizeBytes { get; init; }

    public required long MemoryLoadBytes { get; init; }

    public required long TotalAvailableMemoryBytes { get; init; }
}

internal sealed class HealthGcMetrics
{
    public required int Gen0Collections { get; init; }

    public required int Gen1Collections { get; init; }

    public required int Gen2Collections { get; init; }
}

internal sealed class HealthPerformanceErrorResponse
{
    public required string Status { get; init; }

    public required string Message { get; init; }

    public string? Details { get; init; }
}

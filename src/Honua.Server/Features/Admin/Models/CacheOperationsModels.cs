// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

internal sealed class CacheHealthResponse
{
    public bool IsHealthy { get; init; }

    public bool IsUsingFallback { get; init; }

    public bool CacheEnabled { get; init; }

    public bool FallbackEnabled { get; init; }

    public string? KeyPrefix { get; init; }

    public int DefaultTtlSeconds { get; init; }

    public int RetryIntervalSeconds { get; init; }

    public RedisServerInfoResponse? RedisServerInfo { get; init; }

    public string? MetricsEndpoint { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }
}

internal sealed class CacheStatisticsResponse
{
    public required DateTimeOffset GeneratedAt { get; init; }
    public required long Hits { get; init; }
    public required long Misses { get; init; }
    public required long Evictions { get; init; }
    public required double HitRatio { get; init; }
    public required double MissRatio { get; init; }
    public required string Backend { get; init; }
    public required bool IsUsingFallback { get; init; }
    public required int LocalKeyCount { get; init; }
    public long? DistributedKeyCount { get; init; }
    public long? RedisDatabaseSize { get; init; }
    public long? RedisUsedMemoryBytes { get; init; }
    public string? RedisUsedMemoryHuman { get; init; }
    public required IReadOnlyDictionary<string, CacheTypeStatisticsResponse> Types { get; init; }
}

internal sealed class CacheTypeStatisticsResponse
{
    public required long Hits { get; init; }
    public required long Misses { get; init; }
    public required long Evictions { get; init; }
    public required double HitRatio { get; init; }
    public required double MissRatio { get; init; }
}

internal sealed class RedisConnectionMetricsResponse
{
    public required DateTimeOffset GeneratedAt { get; init; }
    public required bool IsConfigured { get; init; }
    public required bool IsConnected { get; init; }
    public string? ClientName { get; init; }
    public long? TotalOutstanding { get; init; }
    public int? EndpointCount { get; init; }
    public RedisServerInfoResponse? ServerInfo { get; init; }
}

internal sealed class RedisServerInfoResponse
{
    public bool IsConnected { get; init; }

    public string? RedisVersion { get; init; }

    public string? UsedMemory { get; init; }

    public int? ConnectedClients { get; init; }

    public long? DatabaseSize { get; init; }

    public long? UptimeSeconds { get; init; }
}

internal sealed class CacheOperationsInvalidationRequest
{
    public string? KeyPattern { get; init; }

    public string? ServiceId { get; init; }

    public int? LayerId { get; init; }

    public string? Scope { get; init; }
}

internal sealed class CacheOperationsInvalidationResponse
{
    public bool Invalidated { get; init; }

    public string? Scope { get; init; }

    public string? Detail { get; init; }
}

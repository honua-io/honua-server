// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response for cache health status including Redis connection state.
/// </summary>
internal sealed class CacheHealthResponse
{
    /// <summary>
    /// Whether the cache service is healthy and responsive.
    /// </summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// Whether the cache is currently using in-memory fallback mode.
    /// </summary>
    public bool IsUsingFallback { get; init; }

    /// <summary>
    /// Whether caching is enabled in configuration.
    /// </summary>
    public bool CacheEnabled { get; init; }

    /// <summary>
    /// Whether fallback mode is enabled in configuration.
    /// </summary>
    public bool FallbackEnabled { get; init; }

    /// <summary>
    /// Key prefix used for all cache entries.
    /// </summary>
    public string? KeyPrefix { get; init; }

    /// <summary>
    /// Default time-to-live in seconds for cached entries.
    /// </summary>
    public int DefaultTtlSeconds { get; init; }

    /// <summary>
    /// Interval in seconds between Redis reconnection attempts.
    /// </summary>
    public int RetryIntervalSeconds { get; init; }

    /// <summary>
    /// Redis server information when connected, null when Redis is not configured.
    /// </summary>
    public RedisServerInfoResponse? RedisServerInfo { get; init; }

    /// <summary>
    /// Pointer to the detailed metrics endpoint for hit/miss breakdowns.
    /// Null until the metrics/cache endpoint is implemented.
    /// </summary>
    public string? MetricsEndpoint { get; init; }

    /// <summary>
    /// Timestamp when this response was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// Redis server connection and statistics information.
/// </summary>
internal sealed class RedisServerInfoResponse
{
    /// <summary>
    /// Whether the Redis connection is currently active.
    /// </summary>
    public bool IsConnected { get; init; }

    /// <summary>
    /// Redis server version string.
    /// </summary>
    public string? RedisVersion { get; init; }

    /// <summary>
    /// Memory usage reported by Redis.
    /// </summary>
    public string? UsedMemory { get; init; }

    /// <summary>
    /// Number of clients connected to Redis.
    /// </summary>
    public int? ConnectedClients { get; init; }

    /// <summary>
    /// Number of keys in the current database.
    /// </summary>
    public long? DatabaseSize { get; init; }

    /// <summary>
    /// Redis server uptime in seconds.
    /// </summary>
    public long? UptimeSeconds { get; init; }
}

/// <summary>
/// Request body for cache invalidation operations.
/// </summary>
internal sealed class CacheOperationsInvalidationRequest
{
    /// <summary>
    /// Key pattern for pattern-based invalidation (e.g., "layer:*").
    /// </summary>
    public string? KeyPattern { get; init; }

    /// <summary>
    /// Service ID for service-scoped invalidation.
    /// </summary>
    public string? ServiceId { get; init; }

    /// <summary>
    /// Layer ID for layer-scoped invalidation (requires ServiceId).
    /// </summary>
    public int? LayerId { get; init; }

    /// <summary>
    /// Named scope for predefined invalidation targets: "ogc-metadata".
    /// </summary>
    public string? Scope { get; init; }
}

/// <summary>
/// Response for a cache invalidation operation.
/// </summary>
internal sealed class CacheOperationsInvalidationResponse
{
    /// <summary>
    /// Whether the invalidation was performed.
    /// </summary>
    public bool Invalidated { get; init; }

    /// <summary>
    /// The scope that was invalidated.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Additional detail about what was invalidated.
    /// </summary>
    public string? Detail { get; init; }
}

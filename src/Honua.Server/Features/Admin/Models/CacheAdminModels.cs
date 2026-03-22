// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response model for cache health status.
/// </summary>
public sealed class CacheStatusResponse
{
    /// <summary>
    /// Whether the cache service is healthy.
    /// </summary>
    public required bool IsHealthy { get; init; }

    /// <summary>
    /// Whether the cache is using the in-memory fallback mode.
    /// </summary>
    public required bool IsUsingFallback { get; init; }

    /// <summary>
    /// Timestamp when the status was checked.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Request model for cache invalidation.
/// </summary>
public sealed class CacheInvalidationRequest
{
    /// <summary>
    /// Invalidation scope: layer, service, collection, ogc-metadata, or all.
    /// </summary>
    public required string Scope { get; init; }

    /// <summary>
    /// Service ID for layer or service scoped invalidation.
    /// </summary>
    public string? ServiceId { get; init; }

    /// <summary>
    /// Layer ID for layer scoped invalidation.
    /// </summary>
    public int? LayerId { get; init; }

    /// <summary>
    /// Collection ID for collection scoped invalidation.
    /// </summary>
    public string? CollectionId { get; init; }
}

/// <summary>
/// Response model for cache invalidation result.
/// </summary>
public sealed class CacheInvalidationResponse
{
    /// <summary>
    /// Whether the invalidation was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Invalidation scope that was applied.
    /// </summary>
    public required string Scope { get; init; }

    /// <summary>
    /// Descriptive message about the result.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Timestamp when the invalidation was performed.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }
}

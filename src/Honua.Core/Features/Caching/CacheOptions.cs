// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Features.Caching;

/// <summary>
/// Configuration options for the Redis metadata cache.
/// </summary>
public sealed class CacheOptions
{
    /// <summary>
    /// Configuration section name for appsettings.json binding.
    /// </summary>
    public const string SectionName = "Cache";

    /// <summary>
    /// Whether caching is enabled. Default is true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default time-to-live for cached layer metadata in seconds.
    /// Default is 300 seconds (5 minutes).
    /// </summary>
    [Range(1, 86400, ErrorMessage = "DefaultTtlSeconds must be between 1 and 86400 (24 hours)")]
    public int DefaultTtlSeconds { get; set; } = 300;

    /// <summary>
    /// Time-to-live for cached service metadata in seconds.
    /// Default is 300 seconds (5 minutes).
    /// </summary>
    [Range(1, 86400, ErrorMessage = "ServiceTtlSeconds must be between 1 and 86400 (24 hours)")]
    public int ServiceTtlSeconds { get; set; } = 300;

    /// <summary>
    /// Time-to-live for cached layer metadata in seconds.
    /// Default is 300 seconds (5 minutes).
    /// </summary>
    [Range(1, 86400, ErrorMessage = "LayerTtlSeconds must be between 1 and 86400 (24 hours)")]
    public int LayerTtlSeconds { get; set; } = 300;

    /// <summary>
    /// Whether to use in-memory fallback when Redis is unavailable.
    /// Default is true for high availability.
    /// </summary>
    public bool EnableFallback { get; set; } = true;

    /// <summary>
    /// Maximum number of entries in the in-memory fallback cache.
    /// Default is 1000 entries.
    /// </summary>
    [Range(10, 100000, ErrorMessage = "FallbackMaxEntries must be between 10 and 100000")]
    public int FallbackMaxEntries { get; set; } = 1000;

    /// <summary>
    /// Interval in seconds to retry connecting to Redis after a failure.
    /// Default is 30 seconds.
    /// </summary>
    [Range(5, 300, ErrorMessage = "RetryIntervalSeconds must be between 5 and 300")]
    public int RetryIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Key prefix for all cache entries. Helps avoid collisions in shared Redis instances.
    /// Default is "honua:".
    /// </summary>
    public string KeyPrefix { get; set; } = "honua:";

    /// <summary>
    /// Gets the default TTL as a TimeSpan.
    /// </summary>
    public TimeSpan DefaultTtl => TimeSpan.FromSeconds(DefaultTtlSeconds);

    /// <summary>
    /// Gets the service TTL as a TimeSpan.
    /// </summary>
    public TimeSpan ServiceTtl => TimeSpan.FromSeconds(ServiceTtlSeconds);

    /// <summary>
    /// Gets the layer TTL as a TimeSpan.
    /// </summary>
    public TimeSpan LayerTtl => TimeSpan.FromSeconds(LayerTtlSeconds);

    /// <summary>
    /// Gets the retry interval as a TimeSpan.
    /// </summary>
    public TimeSpan RetryInterval => TimeSpan.FromSeconds(RetryIntervalSeconds);
}

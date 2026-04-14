// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Configuration;
using Honua.Core.Configuration.Validation;
using Honua.Core.Features.Shared.Models;

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
    /// Default is 1800 seconds (30 minutes).
    /// </summary>
    [ValidTtl(1, 24)]
    [ConfigurationPath(SectionName)]
    [SuggestedFix("Use StandardTtl configuration for consistent cache timing")]
    [Range(1, 86400, ErrorMessage = ErrorMessages.RangeValidation.DefaultTtlSeconds)]
    public int DefaultTtlSeconds { get; set; } = TimeConstants.ThirtyMinutes;

    /// <summary>
    /// Time-to-live for cached service metadata in seconds.
    /// Default is 3600 seconds (1 hour).
    /// </summary>
    [ValidTtl(1, 24)]
    [ConfigurationPath(SectionName)]
    [SuggestedFix("Service metadata should use Medium or Long TTL category")]
    [Range(1, 86400, ErrorMessage = ErrorMessages.RangeValidation.ServiceTtlSeconds)]
    public int ServiceTtlSeconds { get; set; } = 3600;

    /// <summary>
    /// Time-to-live for cached layer metadata in seconds.
    /// Default is 1800 seconds (30 minutes).
    /// </summary>
    [Range(1, 86400, ErrorMessage = ErrorMessages.RangeValidation.LayerTtlSeconds)]
    public int LayerTtlSeconds { get; set; } = TimeConstants.ThirtyMinutes;

    /// <summary>
    /// Time-to-live for cached query responses in seconds.
    /// Default is 30 seconds for short-lived response caching.
    /// </summary>
    [Range(1, 3600, ErrorMessage = ErrorMessages.RangeValidation.QueryTtlSeconds)]
    public int QueryTtlSeconds { get; set; } = TimeConstants.ThirtySeconds;

    /// <summary>
    /// Time-to-live for negative cache entries (missing layers/services) in seconds.
    /// Default is 60 seconds to avoid repeated lookups without long-lived false negatives.
    /// </summary>
    [Range(1, 3600, ErrorMessage = ErrorMessages.RangeValidation.NegativeTtlSeconds)]
    public int NegativeTtlSeconds { get; set; } = TimeConstants.OneMinute;

    /// <summary>
    /// Maximum jitter percentage to apply to TTL values to prevent cache stampedes.
    /// Default is 20% (0.2). Range 0-50% to maintain predictable cache behavior.
    /// </summary>
    [Range(0.0, 0.5, ErrorMessage = ErrorMessages.RangeValidation.JitterPercentage)]
    public double JitterPercentage { get; set; } = 0.2;

    /// <summary>
    /// Whether to use in-memory fallback when Redis is unavailable.
    /// Default is true for high availability.
    /// </summary>
    public bool EnableFallback { get; set; } = true;

    /// <summary>
    /// Maximum number of entries in the in-memory fallback cache.
    /// Default is 1000 entries.
    /// </summary>
    [Range(10, 100000, ErrorMessage = ErrorMessages.RangeValidation.FallbackMaxEntries)]
    public int FallbackMaxEntries { get; set; } = 1000;

    /// <summary>
    /// Maximum number of query response entries retained in the in-memory response cache.
    /// Default is 10000 entries.
    /// </summary>
    [Range(100, 500000)]
    public int ResponseCacheMaxEntries { get; set; } = 10000;

    /// <summary>
    /// Interval in seconds to retry connecting to Redis after a failure.
    /// Default is 30 seconds.
    /// </summary>
    [Range(5, 300, ErrorMessage = ErrorMessages.RangeValidation.RetryIntervalSeconds)]
    public int RetryIntervalSeconds { get; set; } = TimeConstants.ThirtySeconds;

    /// <summary>
    /// Key prefix for all cache entries. Helps avoid collisions in shared Redis instances.
    /// Default is "honua:".
    /// </summary>
    public string KeyPrefix { get; set; } = "honua:";

    /// <summary>
    /// Whether background cache refresh (stale-while-revalidate) is enabled.
    /// When enabled, near-expiry cache entries are served stale while a background task refreshes them.
    /// Default is true.
    /// </summary>
    public bool BackgroundRefreshEnabled { get; set; } = true;

    /// <summary>
    /// Percentage of TTL remaining that triggers a background refresh (0.0-1.0).
    /// For example, 0.25 means refresh when 25% or less of the TTL remains.
    /// Default is 0.25 (25%).
    /// </summary>
    [Range(0.05, 0.75, ErrorMessage = ErrorMessages.RangeValidation.BackgroundRefreshThreshold)]
    public double BackgroundRefreshThreshold { get; set; } = 0.25;

    /// <summary>
    /// Maximum number of concurrent background refresh operations.
    /// Default is 10.
    /// </summary>
    [Range(1, 100, ErrorMessage = ErrorMessages.RangeValidation.MaxConcurrentRefreshes)]
    public int MaxConcurrentRefreshes { get; set; } = 10;

    /// <summary>
    /// Timeout in seconds for each background refresh operation.
    /// Default is 30 seconds.
    /// </summary>
    [Range(5, 120, ErrorMessage = ErrorMessages.RangeValidation.RefreshTimeoutSeconds)]
    public int RefreshTimeoutSeconds { get; set; } = TimeConstants.ThirtySeconds;

    /// <summary>
    /// Gets the refresh timeout as a TimeSpan.
    /// </summary>
    public TimeSpan RefreshTimeout => TimeSpan.FromSeconds(RefreshTimeoutSeconds);

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
    /// Gets the negative TTL as a TimeSpan.
    /// </summary>
    public TimeSpan NegativeTtl => TimeSpan.FromSeconds(NegativeTtlSeconds);

    /// <summary>
    /// Gets the query response TTL as a TimeSpan.
    /// </summary>
    public TimeSpan QueryTtl => TimeSpan.FromSeconds(QueryTtlSeconds);

    /// <summary>
    /// Gets the retry interval as a TimeSpan.
    /// </summary>
    public TimeSpan RetryInterval => TimeSpan.FromSeconds(RetryIntervalSeconds);

    /// <summary>
    /// Gets the default TTL with jitter applied to prevent cache stampedes.
    /// </summary>
    /// <returns>TTL with random jitter between 80%-120% of base value (with 20% jitter)</returns>
    public TimeSpan GetDefaultTtlWithJitter()
    {
        return ApplyJitter(DefaultTtl);
    }

    /// <summary>
    /// Gets the service TTL with jitter applied to prevent cache stampedes.
    /// </summary>
    /// <returns>TTL with random jitter applied</returns>
    public TimeSpan GetServiceTtlWithJitter()
    {
        return ApplyJitter(ServiceTtl);
    }

    /// <summary>
    /// Gets the layer TTL with jitter applied to prevent cache stampedes.
    /// </summary>
    /// <returns>TTL with random jitter applied</returns>
    public TimeSpan GetLayerTtlWithJitter()
    {
        return ApplyJitter(LayerTtl);
    }

    /// <summary>
    /// Gets the query response TTL with jitter applied to prevent cache stampedes.
    /// </summary>
    /// <returns>TTL with random jitter applied</returns>
    public TimeSpan GetQueryTtlWithJitter()
    {
        return ApplyJitter(QueryTtl);
    }

    /// <summary>
    /// Gets the negative TTL with jitter applied to prevent cache stampedes.
    /// </summary>
    /// <returns>TTL with random jitter applied</returns>
    public TimeSpan GetNegativeTtlWithJitter()
    {
        return ApplyJitter(NegativeTtl);
    }

    /// <summary>
    /// Applies jitter to a TTL value to prevent cache stampedes.
    /// Jitter is applied as a random percentage between (1 - JitterPercentage) and (1 + JitterPercentage).
    /// </summary>
    /// <param name="baseTtl">Base TTL value</param>
    /// <returns>TTL with jitter applied</returns>
    private TimeSpan ApplyJitter(TimeSpan baseTtl)
    {
        if (JitterPercentage <= 0)
        {
            return baseTtl;
        }

        // Generate random jitter between -JitterPercentage and +JitterPercentage
        var jitterMultiplier = 1.0 + (Random.Shared.NextDouble() - 0.5) * 2 * JitterPercentage;
        var jitteredSeconds = baseTtl.TotalSeconds * jitterMultiplier;

        // Ensure minimum 1 second TTL
        jitteredSeconds = Math.Max(1, jitteredSeconds);

        return TimeSpan.FromSeconds(jitteredSeconds);
    }
}

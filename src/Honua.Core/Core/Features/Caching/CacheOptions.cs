// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Configuration;
using Honua.Core.Configuration.Validation;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Caching;

public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    public bool Enabled { get; set; } = true;

    public bool ResponseCachingEnabled { get; set; }

    public bool ResponseCachingAllowInMemoryFallback { get; set; }

    [Range(1024, 268435456, ErrorMessage = "ResponseCacheMaxEntryBytes must be between 1024 and 268435456.")]
    public int ResponseCacheMaxEntryBytes { get; set; } = 1_048_576;

    [Range(65536, 1073741824, ErrorMessage = "ResponseCacheMaxBytes must be between 65536 and 1073741824.")]
    public int ResponseCacheMaxBytes { get; set; } = 67_108_864;

    [ValidTtl(1, 24)]
    [ConfigurationPath(SectionName)]
    [SuggestedFix("Use StandardTtl configuration for consistent cache timing")]
    [Range(1, 86400, ErrorMessage = ErrorMessages.RangeValidation.DefaultTtlSeconds)]
    public int DefaultTtlSeconds { get; set; } = TimeConstants.ThirtyMinutes;

    [ValidTtl(1, 24)]
    [ConfigurationPath(SectionName)]
    [SuggestedFix("Service metadata should use Medium or Long TTL category")]
    [Range(1, 86400, ErrorMessage = ErrorMessages.RangeValidation.ServiceTtlSeconds)]
    public int ServiceTtlSeconds { get; set; } = 3600;

    [Range(1, 86400, ErrorMessage = ErrorMessages.RangeValidation.LayerTtlSeconds)]
    public int LayerTtlSeconds { get; set; } = TimeConstants.ThirtyMinutes;

    [Range(1, 3600, ErrorMessage = ErrorMessages.RangeValidation.QueryTtlSeconds)]
    public int QueryTtlSeconds { get; set; } = TimeConstants.ThirtySeconds;

    [Range(1, 3600, ErrorMessage = ErrorMessages.RangeValidation.NegativeTtlSeconds)]
    public int NegativeTtlSeconds { get; set; } = TimeConstants.OneMinute;

    [Range(0.0, 0.5, ErrorMessage = ErrorMessages.RangeValidation.JitterPercentage)]
    public double JitterPercentage { get; set; } = 0.2;

    public bool MetadataGraphCacheEnabled { get; set; } = true;

    [Range(0, 3600, ErrorMessage = "MetadataGraphTtlSeconds must be between 0 and 3600 seconds.")]
    public int MetadataGraphTtlSeconds { get; set; } = 10;

    // When unset, request revalidation turns on for Lambda with no Redis invalidation, so one instance sees another instance's publish before the TTL.
    public bool? MetadataGraphRevalidateOnRead { get; set; }

    public bool EnableFallback { get; set; } = true;

    [Range(10, 100000, ErrorMessage = ErrorMessages.RangeValidation.FallbackMaxEntries)]
    public int FallbackMaxEntries { get; set; } = 1000;

    [Range(100, 500000)]
    public int ResponseCacheMaxEntries { get; set; } = 10000;

    [Range(5, 300, ErrorMessage = ErrorMessages.RangeValidation.RetryIntervalSeconds)]
    public int RetryIntervalSeconds { get; set; } = TimeConstants.ThirtySeconds;

    public string KeyPrefix { get; set; } = "honua:";

    public bool BackgroundRefreshEnabled { get; set; } = true;

    [Range(0.05, 0.75, ErrorMessage = ErrorMessages.RangeValidation.BackgroundRefreshThreshold)]
    public double BackgroundRefreshThreshold { get; set; } = 0.25;

    [Range(1, 100, ErrorMessage = ErrorMessages.RangeValidation.MaxConcurrentRefreshes)]
    public int MaxConcurrentRefreshes { get; set; } = 10;

    [Range(5, 120, ErrorMessage = ErrorMessages.RangeValidation.RefreshTimeoutSeconds)]
    public int RefreshTimeoutSeconds { get; set; } = TimeConstants.ThirtySeconds;

    public TimeSpan RefreshTimeout => TimeSpan.FromSeconds(RefreshTimeoutSeconds);

    public TimeSpan DefaultTtl => TimeSpan.FromSeconds(DefaultTtlSeconds);

    public TimeSpan ServiceTtl => TimeSpan.FromSeconds(ServiceTtlSeconds);

    public TimeSpan LayerTtl => TimeSpan.FromSeconds(LayerTtlSeconds);

    public TimeSpan NegativeTtl => TimeSpan.FromSeconds(NegativeTtlSeconds);

    public TimeSpan QueryTtl => TimeSpan.FromSeconds(QueryTtlSeconds);

    public TimeSpan MetadataGraphTtl => TimeSpan.FromSeconds(MetadataGraphTtlSeconds);

    public TimeSpan RetryInterval => TimeSpan.FromSeconds(RetryIntervalSeconds);

    public TimeSpan GetDefaultTtlWithJitter()
    {
        return ApplyJitter(DefaultTtl);
    }

    public TimeSpan GetServiceTtlWithJitter()
    {
        return ApplyJitter(ServiceTtl);
    }

    public TimeSpan GetLayerTtlWithJitter()
    {
        return ApplyJitter(LayerTtl);
    }

    public TimeSpan GetQueryTtlWithJitter()
    {
        return ApplyJitter(QueryTtl);
    }

    public TimeSpan GetNegativeTtlWithJitter()
    {
        return ApplyJitter(NegativeTtl);
    }

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

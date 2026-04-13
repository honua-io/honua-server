// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.ServiceRegistration;

namespace Honua.Core.Features.Caching;

/// <summary>
/// Validates CacheOptions configuration to ensure consistent and safe cache behavior.
/// Enforces complex business rules beyond individual DataAnnotations.
/// </summary>
public sealed class CacheOptionsValidator : ConfigurationValidator<CacheOptions>
{
    /// <summary>
    /// Validates the cache options configuration using derived class-specific logic.
    /// </summary>
    /// <param name="options">The cache options instance to validate</param>
    /// <param name="errors">List to add validation errors to</param>
    protected override void PerformFeatureSpecificValidation(CacheOptions options, List<string> errors)
    {
        // Complex business rule validations
        ValidateTtlLogic(options, errors);
        ValidateFallbackConfiguration(options, errors);
        ValidateKeyPrefix(options, errors);
        ValidateBackgroundRefresh(options, errors);
    }


    /// <summary>
    /// Validates TTL configuration logic.
    /// </summary>
    private static void ValidateTtlLogic(CacheOptions options, List<string> errors)
    {
        // When caching is enabled, TTL values must be positive
        if (options.Enabled)
        {
            ValidateRange(options.DefaultTtlSeconds, 1, int.MaxValue, "DefaultTtlSeconds", errors);
            ValidateRange(options.ServiceTtlSeconds, 1, int.MaxValue, "ServiceTtlSeconds", errors);
            ValidateRange(options.LayerTtlSeconds, 1, int.MaxValue, "LayerTtlSeconds", errors);
            ValidateRange(options.QueryTtlSeconds, 1, int.MaxValue, "QueryTtlSeconds", errors);
            ValidateRange(options.NegativeTtlSeconds, 1, int.MaxValue, "NegativeTtlSeconds", errors);
        }

        // Service TTL should typically be longer than layer TTL for performance
        ValidateLogicalOrder(options.LayerTtlSeconds, options.ServiceTtlSeconds, "LayerTtlSeconds", "ServiceTtlSeconds", errors);

        // Negative TTL should be much shorter than positive TTLs to avoid long-lived false negatives
        var maxPositiveTtl = Math.Max(Math.Max(options.DefaultTtlSeconds, options.ServiceTtlSeconds), options.LayerTtlSeconds);
        var maxRecommendedNegativeTtl = maxPositiveTtl / 10;
        if (options.NegativeTtlSeconds > maxRecommendedNegativeTtl)
        {
            errors.Add($"NegativeTtlSeconds ({options.NegativeTtlSeconds}) should be much shorter than positive TTLs (max: {maxPositiveTtl}) to avoid long-lived false negatives");
        }

        // JitterPercentage validation
        ValidateRange(options.JitterPercentage, 0.0, 0.5, "JitterPercentage", errors);
    }

    /// <summary>
    /// Validates fallback configuration.
    /// </summary>
    private static void ValidateFallbackConfiguration(CacheOptions options, List<string> errors)
    {
        // When fallback is enabled, validate fallback settings
        if (options.EnableFallback)
        {
            ValidateRange(options.FallbackMaxEntries, 1, 100000, "FallbackMaxEntries", errors);
            ValidateRange(options.RetryIntervalSeconds, 5, 300, "RetryIntervalSeconds", errors);
        }

        ValidateRange(options.ResponseCacheMaxEntries, 100, 500000, "ResponseCacheMaxEntries", errors);
    }

    /// <summary>
    /// Validates background refresh configuration.
    /// </summary>
    private static void ValidateBackgroundRefresh(CacheOptions options, List<string> errors)
    {
        if (!options.BackgroundRefreshEnabled)
            return;

        ValidateRange(options.BackgroundRefreshThreshold, 0.05, 0.75, "BackgroundRefreshThreshold", errors);
        ValidateRange(options.MaxConcurrentRefreshes, 1, 100, "MaxConcurrentRefreshes", errors);
        ValidateRange(options.RefreshTimeoutSeconds, 5, 120, "RefreshTimeoutSeconds", errors);
    }

    /// <summary>
    /// Validates key prefix configuration.
    /// </summary>
    private static void ValidateKeyPrefix(CacheOptions options, List<string> errors)
    {
        ValidateRequiredString(options.KeyPrefix, "KeyPrefix", errors);

        if (!string.IsNullOrWhiteSpace(options.KeyPrefix))
        {
            ValidateStringLength(options.KeyPrefix, 50, "KeyPrefix", errors);

            // Should end with separator for clean key hierarchies
            if (!options.KeyPrefix.EndsWith(':') && !options.KeyPrefix.EndsWith('/'))
            {
                errors.Add("KeyPrefix should end with ':' or '/' to create clean key hierarchies (e.g., 'honua:')");
            }

            // Should not contain invalid characters
            var invalidChars = new[] { ' ', '\t', '\n', '\r', '*', '?', '[', ']' };
            if (options.KeyPrefix.IndexOfAny(invalidChars) >= 0)
            {
                errors.Add("KeyPrefix contains invalid characters - avoid spaces, wildcards, and special characters");
            }
        }
    }
}

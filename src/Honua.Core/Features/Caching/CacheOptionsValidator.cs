// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Core.Features.Caching;

/// <summary>
/// Validates CacheOptions configuration to ensure consistent and safe cache behavior.
/// Enforces complex business rules beyond individual DataAnnotations.
/// </summary>
public sealed class CacheOptionsValidator : OptionsValidator<CacheOptions>
{
    /// <summary>
    /// Validates the cache options configuration using derived class-specific logic.
    /// </summary>
    /// <param name="options">The cache options instance to validate</param>
    /// <param name="failures">List to add validation errors to</param>
    protected override void ValidateOptions(CacheOptions options, List<string> failures)
    {
        // Complex business rule validations
        ValidateTtlLogic(options, failures);
        ValidateFallbackConfiguration(options, failures);
        ValidateKeyPrefix(options, failures);
    }


    /// <summary>
    /// Validates TTL configuration logic.
    /// </summary>
    private static void ValidateTtlLogic(CacheOptions options, List<string> failures)
    {
        // When caching is enabled, TTL values must be positive
        if (options.Enabled)
        {
            ValidateRange(options.DefaultTtlSeconds, 1, int.MaxValue, "DefaultTtlSeconds", failures);
            ValidateRange(options.ServiceTtlSeconds, 1, int.MaxValue, "ServiceTtlSeconds", failures);
            ValidateRange(options.LayerTtlSeconds, 1, int.MaxValue, "LayerTtlSeconds", failures);
            ValidateRange(options.QueryTtlSeconds, 1, int.MaxValue, "QueryTtlSeconds", failures);
            ValidateRange(options.NegativeTtlSeconds, 1, int.MaxValue, "NegativeTtlSeconds", failures);
        }

        // Service TTL should typically be longer than layer TTL for performance
        ValidateLogicalOrder(options.LayerTtlSeconds, options.ServiceTtlSeconds, "LayerTtlSeconds", "ServiceTtlSeconds", failures);

        // Negative TTL should be much shorter than positive TTLs to avoid long-lived false negatives
        var maxPositiveTtl = Math.Max(Math.Max(options.DefaultTtlSeconds, options.ServiceTtlSeconds), options.LayerTtlSeconds);
        var maxRecommendedNegativeTtl = maxPositiveTtl / 10;
        if (options.NegativeTtlSeconds > maxRecommendedNegativeTtl)
        {
            failures.Add($"NegativeTtlSeconds ({options.NegativeTtlSeconds}) should be much shorter than positive TTLs (max: {maxPositiveTtl}) to avoid long-lived false negatives");
        }

        // JitterPercentage validation
        ValidateRange(options.JitterPercentage, 0.0, 0.5, "JitterPercentage", failures);
    }

    /// <summary>
    /// Validates fallback configuration.
    /// </summary>
    private static void ValidateFallbackConfiguration(CacheOptions options, List<string> failures)
    {
        // When fallback is enabled, validate fallback settings
        if (options.EnableFallback)
        {
            ValidateRange(options.FallbackMaxEntries, 1, 100000, "FallbackMaxEntries", failures);
            ValidateRange(options.RetryIntervalSeconds, 5, 300, "RetryIntervalSeconds", failures);
        }

        ValidateRange(options.ResponseCacheMaxEntries, 100, 500000, "ResponseCacheMaxEntries", failures);
    }

    /// <summary>
    /// Validates key prefix configuration.
    /// </summary>
    private static void ValidateKeyPrefix(CacheOptions options, List<string> failures)
    {
        ValidateRequiredString(options.KeyPrefix, "KeyPrefix", failures);

        if (!string.IsNullOrWhiteSpace(options.KeyPrefix))
        {
            ValidateStringLength(options.KeyPrefix, 50, "KeyPrefix", failures);

            // Should end with separator for clean key hierarchies
            if (!options.KeyPrefix.EndsWith(':') && !options.KeyPrefix.EndsWith('/'))
            {
                failures.Add("KeyPrefix should end with ':' or '/' to create clean key hierarchies (e.g., 'honua:')");
            }

            // Should not contain invalid characters
            var invalidChars = new[] { ' ', '\t', '\n', '\r', '*', '?', '[', ']' };
            if (options.KeyPrefix.IndexOfAny(invalidChars) >= 0)
            {
                failures.Add("KeyPrefix contains invalid characters - avoid spaces, wildcards, and special characters");
            }
        }
    }
}

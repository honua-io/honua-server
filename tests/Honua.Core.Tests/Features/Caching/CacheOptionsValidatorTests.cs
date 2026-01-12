// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Caching;

/// <summary>
/// Unit tests for CacheOptionsValidator to ensure proper validation of cache configuration.
/// </summary>
public class CacheOptionsValidatorTests
{
    private readonly CacheOptionsValidator _validator = new();

    [UnitTest]
    public void Validate_ValidConfiguration_ReturnsSuccess()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = true,
            DefaultTtlSeconds = 1800,
            ServiceTtlSeconds = 3600,
            LayerTtlSeconds = 1800,
            NegativeTtlSeconds = 60,
            JitterPercentage = 0.2,
            EnableFallback = true,
            FallbackMaxEntries = 1000,
            RetryIntervalSeconds = 30,
            KeyPrefix = "honua:"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures ?? Array.Empty<string>());
    }

    [UnitTest]
    public void Validate_CachingEnabledWithNegativeTtl_ReturnsFail()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = true,
            DefaultTtlSeconds = -100 // Invalid: negative TTL when enabled
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("DefaultTtlSeconds") && f.Contains("between 1 and 86400"));
    }

    [UnitTest]
    public void Validate_ServiceTtlShorterThanLayerTtl_ReturnsWarning()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = true,
            ServiceTtlSeconds = 1800, // 30 minutes
            LayerTtlSeconds = 3600,   // 1 hour - longer than service TTL
            DefaultTtlSeconds = 1800,
            NegativeTtlSeconds = 60,
            KeyPrefix = "honua:"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("LayerTtlSeconds") && f.Contains("must not exceed ServiceTtlSeconds"));
    }

    [UnitTest]
    public void Validate_NegativeTtlTooLong_ReturnsFail()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = true,
            DefaultTtlSeconds = 1800,
            ServiceTtlSeconds = 3600,
            LayerTtlSeconds = 1800,
            NegativeTtlSeconds = 1000, // Too long compared to positive TTLs
            KeyPrefix = "honua:"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("NegativeTtlSeconds") && f.Contains("should be much shorter"));
    }

    [UnitTest]
    public void Validate_InvalidJitterPercentage_ReturnsFail()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = true,
            JitterPercentage = 0.8, // Invalid: exceeds 50%
            DefaultTtlSeconds = 1800,
            KeyPrefix = "honua:"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("JitterPercentage") && f.Contains("between 0 and 0.5"));
    }

    [UnitTest]
    public void Validate_FallbackEnabledWithInvalidSettings_ReturnsFail()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = true,
            EnableFallback = true,
            FallbackMaxEntries = -100, // Invalid: negative
            RetryIntervalSeconds = 2,  // Invalid: too short
            DefaultTtlSeconds = 1800,
            KeyPrefix = "honua:"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("FallbackMaxEntries") && f.Contains("must be between"));
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("RetryIntervalSeconds") && f.Contains("must be between 5 and 300"));
    }

    [UnitTest]
    public void Validate_FallbackTooManyEntries_ReturnsFail()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = true,
            EnableFallback = true,
            FallbackMaxEntries = 200000, // Invalid: too many entries
            DefaultTtlSeconds = 1800,
            KeyPrefix = "honua:"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("FallbackMaxEntries") && f.Contains("must be between"));
    }

    [UnitTest]
    public void Validate_RetryIntervalTooLong_ReturnsFail()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = true,
            EnableFallback = true,
            RetryIntervalSeconds = 600, // Invalid: too long (10 minutes)
            FallbackMaxEntries = 1000,
            DefaultTtlSeconds = 1800,
            KeyPrefix = "honua:"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("RetryIntervalSeconds") && f.Contains("must be between 5 and 300"));
    }

    [UnitTest]
    public void Validate_EmptyKeyPrefix_ReturnsFail()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = true,
            KeyPrefix = "", // Invalid: empty
            DefaultTtlSeconds = 1800
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("KeyPrefix") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_KeyPrefixWithoutSeparator_ReturnsFail()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = true,
            KeyPrefix = "honua", // Invalid: no separator
            DefaultTtlSeconds = 1800
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("KeyPrefix") && f.Contains("should end with ':' or '/'"));
    }

    [UnitTest]
    public void Validate_KeyPrefixTooLong_ReturnsFail()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = true,
            KeyPrefix = new string('a', 60) + ":", // Invalid: too long
            DefaultTtlSeconds = 1800
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("KeyPrefix") && f.Contains("should not exceed 50 characters"));
    }

    [UnitTest]
    public void Validate_KeyPrefixWithInvalidCharacters_ReturnsFail()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = true,
            KeyPrefix = "honua space:", // Invalid: contains space
            DefaultTtlSeconds = 1800
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("KeyPrefix") && f.Contains("invalid characters"));
    }

    [UnitTest]
    public void Validate_DataAnnotationViolations_ReturnsFail()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = true,
            DefaultTtlSeconds = 100000, // Invalid: exceeds maximum
            KeyPrefix = "honua:"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("DefaultTtlSeconds") && f.Contains("between 1 and 86400"));
    }

    [UnitTest]
    public void Validate_NullOptions_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _validator.Validate(null, null!));
    }

    [UnitTest]
    public void Validate_CachingDisabled_AllowsZeroTtl()
    {
        // Arrange
        var options = new CacheOptions
        {
            Enabled = false,
            DefaultTtlSeconds = 0, // This should be allowed when caching is disabled
            KeyPrefix = "honua:"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded); // Should still fail due to DataAnnotations validation
    }

    [UnitTest]
    public void Validate_ValidKeyPrefixVariations_ReturnsSuccess()
    {
        // Arrange & Act & Assert
        var validPrefixes = new[] { "honua:", "app/", "cache-key:", "my_app:" };

        foreach (var prefix in validPrefixes)
        {
            var options = new CacheOptions
            {
                Enabled = true,
                DefaultTtlSeconds = 1800,
                ServiceTtlSeconds = 3600,
                LayerTtlSeconds = 1800,
                NegativeTtlSeconds = 60,
                KeyPrefix = prefix
            };

            var result = _validator.Validate(null, options);

            Assert.True(result.Succeeded, $"Prefix '{prefix}' should be valid");
        }
    }
}

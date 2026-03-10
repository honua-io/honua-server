// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Geocoding;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Postgres.Tests.Features.Geocoding;

/// <summary>
/// Unit tests for EsriGeocodingOptionsValidator
/// </summary>
public sealed class EsriGeocodingOptionsValidatorTests
{
    private readonly EsriGeocodingOptionsValidator _validator = new();

    [Fact]
    public void Validate_WithValidApiKeyConfiguration_ShouldSucceed()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = "test-api-key",
            TimeoutSeconds = 30,
            MaxResults = 10,
            UserAgent = "Test-Agent"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.Equal(ValidateOptionsResult.Success, result);
    }

    [Fact]
    public void Validate_WithValidOAuthConfiguration_ShouldSucceed()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
            TokenEndpoint = "https://www.arcgis.com/sharing/rest/oauth2/token",
            TimeoutSeconds = 30,
            MaxResults = 10,
            UserAgent = "Test-Agent"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.Equal(ValidateOptionsResult.Success, result);
    }

    [Fact]
    public void Validate_WithMissingBaseUrl_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = string.Empty,
            ApiKey = "test-api-key"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("BaseUrl is required", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithInvalidBaseUrl_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "not-a-valid-url",
            ApiKey = "test-api-key"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("BaseUrl must be a valid absolute URL", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithMissingAuthentication_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = string.Empty,
            ClientId = string.Empty,
            ClientSecret = string.Empty
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("Either ApiKey or both ClientId and ClientSecret must be provided", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithBothApiKeyAndOAuth_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = "test-api-key",
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("Cannot configure both ApiKey and OAuth authentication", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithPartialOAuthCredentials_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ClientId = "test-client-id",
            ClientSecret = string.Empty // Missing client secret
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("Either ApiKey or both ClientId and ClientSecret must be provided", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithMissingTokenEndpoint_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
            TokenEndpoint = string.Empty
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("TokenEndpoint is required when using OAuth authentication", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithInvalidTokenEndpoint_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
            TokenEndpoint = "not-a-valid-url"
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("TokenEndpoint must be a valid absolute URL", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithInvalidTimeoutSeconds_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = "test-api-key",
            TimeoutSeconds = 0 // Invalid
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("TimeoutSeconds must be greater than 0", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithExcessiveTimeoutSeconds_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = "test-api-key",
            TimeoutSeconds = 400 // Too high
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("TimeoutSeconds cannot exceed 300 seconds", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithInvalidMaxResults_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = "test-api-key",
            MaxResults = 100 // Exceeds Esri limit
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("MaxResults cannot exceed 50", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithInvalidBatchSize_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = "test-api-key",
            EnableBatchGeocoding = true,
            MaxBatchSize = 2000 // Exceeds Esri limit
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("MaxBatchSize cannot exceed 1000", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithInvalidSpatialReference_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = "test-api-key",
            DefaultSpatialReference = -1 // Invalid
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("DefaultSpatialReference must be greater than 0", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithInvalidTokenCacheDuration_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
            TokenCacheDurationMinutes = 150 // Too high
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("TokenCacheDurationMinutes cannot exceed 120 minutes", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithInvalidRateLimit_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = "test-api-key",
            RateLimitRequestsPerSecond = 200 // Too high
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("RateLimitRequestsPerSecond cannot exceed 100", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithMissingUserAgent_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = "test-api-key",
            UserAgent = string.Empty
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("UserAgent is required", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithNegativePriority_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = "test-api-key",
            Priority = -1
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("Priority cannot be negative", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithEmptyOutFields_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = "test-api-key",
            DefaultOutFields = Array.Empty<string>()
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("At least one DefaultOutField must be specified", result.FailureMessage);
    }

    [Fact]
    public void Validate_WithInvalidCustomLocator_ShouldFail()
    {
        // Arrange
        var options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = "test-api-key",
            CustomLocators = new Dictionary<string, string>
            {
                ["custom"] = "not-a-valid-url"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("Custom locator 'custom' must have a valid absolute URL", result.FailureMessage);
    }
}

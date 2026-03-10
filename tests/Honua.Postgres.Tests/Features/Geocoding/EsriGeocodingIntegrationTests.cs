// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geocoding;
using Honua.Core.Features.Geocoding.Abstractions;
using Honua.Core.Features.Geocoding.Domain;
using Honua.Postgres.Features.Geocoding;
using Honua.Postgres.Features.Geocoding.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Sdk;

namespace Honua.Postgres.Tests.Features.Geocoding;

/// <summary>
/// Integration tests for Esri geocoding provider
/// These tests require actual Esri API credentials and will be skipped if not available
/// </summary>
public sealed class EsriGeocodingIntegrationTests : IAsyncDisposable
{
    private readonly ServiceProvider? _serviceProvider;
    private readonly IGeocodeProvider? _provider;
    private readonly bool _canRunTests;

    public EsriGeocodingIntegrationTests()
    {
        // Try to get API key from environment variable
        var apiKey = Environment.GetEnvironmentVariable("ESRI_API_KEY");
        _canRunTests = !string.IsNullOrWhiteSpace(apiKey);

        if (_canRunTests)
        {
            var services = new ServiceCollection();

            services.AddLogging(builder => builder.AddConsole());

            // Configure Esri options using instance binding for testing
            var esriOptions = new EsriGeocodingOptions
            {
                ApiKey = apiKey,
                BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
                MaxResults = 5,
                TimeoutSeconds = 30,
                EnableSuggestions = true,
                EnableBatchGeocoding = true,
                UserAgent = "Honua-IntegrationTest/1.0"
            };
            services.AddSingleton(Options.Create(esriOptions));

            // Register Esri provider services manually
            services.AddSingleton<IValidateOptions<EsriGeocodingOptions>, EsriGeocodingOptionsValidator>();
            services.AddHttpClient<EsriGeocodeProvider>();
            services.AddGeocodeProvider<EsriGeocodeProvider>("esri");

            _serviceProvider = services.BuildServiceProvider();
            _provider = _serviceProvider.GetRequiredService<IGeocodeProvider>();
        }
    }

    [Fact]
    public async Task ForwardGeocodeAsync_WithValidAddress_ShouldReturnResults()
    {
        if (!_canRunTests)
        {
            return; // Skip test - ESRI_API_KEY environment variable not set
        }

        // Arrange
        var request = new ForwardGeocodeRequest(
            "1600 Amphitheatre Parkway, Mountain View, CA",
            MaxResults: 3,
            CountryCodes: "US");

        // Act
        var results = await _provider!.ForwardGeocodeAsync(request);

        // Assert
        Assert.NotEmpty(results);

        var firstResult = results[0];
        Assert.NotEmpty(firstResult.Address);
        Assert.True(firstResult.Score > 50); // Should have reasonable confidence
        Assert.InRange(firstResult.X, -123, -121); // Approximate longitude for Mountain View, CA
        Assert.InRange(firstResult.Y, 37, 38); // Approximate latitude for Mountain View, CA
        Assert.Equal(4326, firstResult.SpatialReferenceWkid);
        Assert.Contains("esri", firstResult.Attributes["Provider"]?.ToLowerInvariant() ?? "");
    }

    [Fact]
    public async Task ForwardGeocodeAsync_WithStructuredAddress_ShouldReturnResults()
    {
        if (!_canRunTests)
        {
            return; // Skip test - ESRI_API_KEY environment variable not set
        }

        // Arrange
        var request = new ForwardGeocodeRequest("", InputType: GeocodeInputType.Structured)
        {
            StructuredAddress = new StructuredAddress
            {
                AddressNumber = "1600",
                StreetName = "Amphitheatre Parkway",
                City = "Mountain View",
                Region = "CA",
                Country = "US"
            }
        };

        // Act
        var results = await _provider!.ForwardGeocodeAsync(request);

        // Assert
        Assert.NotEmpty(results);
        var result = results[0];
        Assert.Contains("Amphitheatre", result.Address, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.StructuredAddress);
        Assert.Equal("Mountain View", result.StructuredAddress.City, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReverseGeocodeAsync_WithValidCoordinates_ShouldReturnMatch()
    {
        if (!_canRunTests)
        {
            return; // Skip test - ESRI_API_KEY environment variable not set
        }

        // Arrange - Google headquarters coordinates
        var request = new ReverseGeocodeRequest(
            X: -122.0856,
            Y: 37.4220,
            DistanceMeters: 100);

        // Act
        var result = await _provider!.ReverseGeocodeAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Address);
        Assert.InRange(result.X, -123, -121);
        Assert.InRange(result.Y, 37, 38);
        Assert.NotNull(result.StructuredAddress);
        Assert.Equal("esri", result.Attributes["Provider"]);
    }

    [Fact]
    public async Task SuggestAsync_WithPartialAddress_ShouldReturnSuggestions()
    {
        if (!_canRunTests)
        {
            return; // Skip test - ESRI_API_KEY environment variable not set
        }

        // Arrange
        var request = new SuggestGeocodeRequest(
            "1600 Amphi",
            MaxResults: 5,
            CountryCodes: "US");

        // Act
        var results = await _provider!.SuggestAsync(request);

        // Assert
        Assert.NotEmpty(results);
        Assert.True(results.Any(s => s.Text.Contains("Amphitheatre", StringComparison.OrdinalIgnoreCase)));
        Assert.All(results, suggestion =>
        {
            Assert.NotEmpty(suggestion.Text);
            Assert.NotEmpty(suggestion.MagicKey);
        });
    }

    [Fact]
    public async Task BatchGeocodeAsync_WithMultipleAddresses_ShouldReturnResults()
    {
        if (!_canRunTests)
        {
            return; // Skip test - ESRI_API_KEY environment variable not set
        }

        // Arrange
        var addresses = new[]
        {
            "1600 Amphitheatre Parkway, Mountain View, CA",
            "1 Infinite Loop, Cupertino, CA",
            "410 Terry Ave N, Seattle, WA"
        };

        var request = new BatchGeocodeRequest(addresses, CountryCodes: "US");

        // Act
        var results = await _provider!.BatchGeocodeAsync(request);

        // Assert
        Assert.NotEmpty(results);
        Assert.True(results.Count <= addresses.Length); // May have fewer results if some fail

        Assert.All(results, candidate =>
        {
            Assert.NotEmpty(candidate.Address);
            Assert.True(candidate.Score > 0);
            Assert.NotEmpty(candidate.ProviderId);
        });
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnHealthyStatus()
    {
        if (!_canRunTests)
        {
            return; // Skip test - ESRI_API_KEY environment variable not set
        }

        // Act
        var health = await _provider!.CheckHealthAsync();

        // Assert
        Assert.True(health.IsHealthy);
        Assert.Equal("esri", health.ProviderName);
        Assert.Null(health.ErrorMessage);
        Assert.True(health.ResponseTimeMs > 0);
    }

    [Fact]
    public async Task ForwardGeocodeAsync_WithSearchBounds_ShouldRespectBounds()
    {
        if (!_canRunTests)
        {
            return; // Skip test - ESRI_API_KEY environment variable not set
        }

        // Arrange - Search for "Main Street" only in Seattle area
        var seattleBounds = new GeocodeBounds(
            XMin: -122.5,
            YMin: 47.4,
            XMax: -122.2,
            YMax: 47.8);

        var request = new ForwardGeocodeRequest(
            "Main Street",
            MaxResults: 3,
            SearchBounds: seattleBounds);

        // Act
        var results = await _provider!.ForwardGeocodeAsync(request);

        // Assert
        Assert.NotEmpty(results);
        Assert.All(results, candidate =>
        {
            Assert.InRange(candidate.X, seattleBounds.XMin, seattleBounds.XMax);
            Assert.InRange(candidate.Y, seattleBounds.YMin, seattleBounds.YMax);
        });
    }

    [Fact]
    public async Task ForwardGeocodeAsync_WithDifferentSpatialReference_ShouldReturnCorrectCoordinates()
    {
        if (!_canRunTests)
        {
            return; // Skip test - ESRI_API_KEY environment variable not set
        }

        // Arrange
        var request = new ForwardGeocodeRequest(
            "1600 Amphitheatre Parkway, Mountain View, CA",
            SpatialReferenceWkid: 3857); // Web Mercator

        // Act
        var results = await _provider!.ForwardGeocodeAsync(request);

        // Assert
        Assert.NotEmpty(results);
        var result = results[0];
        Assert.Equal(3857, result.SpatialReferenceWkid);

        // Web Mercator coordinates should be much larger than geographic coordinates
        Assert.True(Math.Abs(result.X) > 10000);
        Assert.True(Math.Abs(result.Y) > 1000000);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }

        _serviceProvider?.Dispose();
    }
}

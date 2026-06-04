// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geocoding.Features.Geocoding.Providers;

namespace Honua.Core.Tests.Features.Geocoding.Providers;

public sealed class NominatimGeocodeProviderTests
{
    [Fact]
    public async Task ForwardGeocodeAsync_AllowsStringEncodedCoordinates()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler())
        {
            BaseAddress = new Uri("https://example.com/", UriKind.Absolute)
        };

        var provider = new NominatimGeocodeProvider(
            new NominatimProviderConfiguration
            {
                BaseUrl = "https://example.com",
                UserAgent = "Honua.Tests/1.0",
                TimeoutSeconds = 10,
                MaxResults = 10,
                MaxSuggestions = 5
            },
            httpClient);

        var results = await provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("10 Downing St", 5, 4326, null),
            CancellationToken.None);

        var candidate = Assert.Single(results);
        Assert.Equal("10 Downing St, London", candidate.Address);
        Assert.Equal(-0.1276d, candidate.X, 4);
        Assert.Equal(51.5034d, candidate.Y, 4);
        Assert.Equal("101", candidate.ProviderId);
    }

    [Fact]
    public async Task ReverseGeocodeAsync_ValidResponse_ReturnsMatch()
    {
        using var httpClient = new HttpClient(new ReverseStubHttpMessageHandler())
        {
            BaseAddress = new Uri("https://example.com/", UriKind.Absolute)
        };

        var provider = new NominatimGeocodeProvider(
            new NominatimProviderConfiguration
            {
                BaseUrl = "https://example.com",
                UserAgent = "Honua.Tests/1.0",
                TimeoutSeconds = 10,
                MaxResults = 10,
                MaxSuggestions = 5
            },
            httpClient);

        var result = await provider.ReverseGeocodeAsync(
            new ReverseGeocodeRequest(-0.1276d, 51.5034d, 4326),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("10 Downing St, London", result.Address);
        Assert.Equal(-0.1276d, result.X, 4);
        Assert.Equal(51.5034d, result.Y, 4);
        Assert.Equal("101", result.ProviderId);
    }

    [Fact]
    public async Task ForwardGeocodeAsync_WithLoopbackHostname_ThrowsBeforeSending()
    {
        var handler = new StubHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://localhost/", UriKind.Absolute)
        };

        var provider = new NominatimGeocodeProvider(
            new NominatimProviderConfiguration
            {
                BaseUrl = "https://localhost",
                UserAgent = "Honua.Tests/1.0",
                TimeoutSeconds = 10,
                MaxResults = 10,
                MaxSuggestions = 5
            },
            httpClient);

        var exception = await Assert.ThrowsAsync<GeocodeProviderException>(() => provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("10 Downing St", 5, 4326, null),
            CancellationToken.None));

        Assert.Equal(GeocodeErrorCodes.InvalidConfiguration, exception.ErrorCode);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task ForwardGeocodeAsync_WithPrivateLiteralAddress_ThrowsBeforeSending()
    {
        var handler = new StubHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://10.0.0.5/", UriKind.Absolute)
        };

        var provider = new NominatimGeocodeProvider(
            new NominatimProviderConfiguration
            {
                BaseUrl = "https://10.0.0.5",
                UserAgent = "Honua.Tests/1.0",
                TimeoutSeconds = 10,
                MaxResults = 10,
                MaxSuggestions = 5
            },
            httpClient);

        var exception = await Assert.ThrowsAsync<GeocodeProviderException>(() => provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("10 Downing St", 5, 4326, null),
            CancellationToken.None));

        Assert.Equal(GeocodeErrorCodes.InvalidConfiguration, exception.ErrorCode);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task ReverseGeocodeAsync_WithLoopbackHostname_ThrowsBeforeSending()
    {
        var handler = new StubHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://localhost/", UriKind.Absolute)
        };

        var provider = new NominatimGeocodeProvider(
            new NominatimProviderConfiguration
            {
                BaseUrl = "https://localhost",
                UserAgent = "Honua.Tests/1.0",
                TimeoutSeconds = 10,
                MaxResults = 10,
                MaxSuggestions = 5
            },
            httpClient);

        var exception = await Assert.ThrowsAsync<GeocodeProviderException>(() => provider.ReverseGeocodeAsync(
            new ReverseGeocodeRequest(-0.1276, 51.5034, 4326, null),
            CancellationToken.None));

        Assert.Equal(GeocodeErrorCodes.InvalidConfiguration, exception.ErrorCode);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public void Capabilities_DefaultConfiguration_AdvertisesSuggestAndBatch()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler())
        {
            BaseAddress = new Uri("https://example.com/", UriKind.Absolute)
        };

        var provider = new NominatimGeocodeProvider(
            new NominatimProviderConfiguration
            {
                BaseUrl = "https://example.com",
                UserAgent = "Honua.Tests/1.0"
            },
            httpClient);

        Assert.True(provider.Capabilities.SupportsSuggest);
        Assert.True(provider.Capabilities.SupportsBatch);
        Assert.True(provider.Capabilities.MaxBatchSize > 0);
    }

    [Fact]
    public async Task SuggestAsync_DefaultConfiguration_ReturnsSuggestionsFromSearch()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler())
        {
            BaseAddress = new Uri("https://example.com/", UriKind.Absolute)
        };

        var provider = new NominatimGeocodeProvider(
            new NominatimProviderConfiguration
            {
                BaseUrl = "https://example.com",
                UserAgent = "Honua.Tests/1.0"
            },
            httpClient);

        var suggestions = await provider.SuggestAsync(
            new SuggestGeocodeRequest("10 Downing", 5),
            CancellationToken.None);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("10 Downing St, London", suggestion.Text);
        Assert.False(string.IsNullOrWhiteSpace(suggestion.MagicKey));
    }

    [Fact]
    public async Task BatchGeocodeAsync_FansOutToForwardGeocode_ReturnsCandidatePerInput()
    {
        var handler = new StubHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.com/", UriKind.Absolute)
        };

        var provider = new NominatimGeocodeProvider(
            new NominatimProviderConfiguration
            {
                BaseUrl = "https://example.com",
                UserAgent = "Honua.Tests/1.0"
            },
            httpClient);

        var results = await provider.BatchGeocodeAsync(
            new BatchGeocodeRequest(["10 Downing St", "350 Fifth Avenue"], 4326),
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, handler.SendCount);
        Assert.All(results, candidate => Assert.Equal("10 Downing St, London", candidate.Address));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"place_id\":101,\"display_name\":\"10 Downing St, London\",\"lat\":\"51.5034\",\"lon\":\"-0.1276\",\"importance\":0.9,\"address\":{\"city\":\"London\"}}]",
                    Encoding.UTF8,
                    "application/json")
            };

            return Task.FromResult(response);
        }
    }

    private sealed class ReverseStubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"place_id\":101,\"display_name\":\"10 Downing St, London\",\"osm_type\":\"way\",\"osm_id\":123,\"address\":{\"road\":\"Downing Street\",\"city\":\"London\"}}",
                    Encoding.UTF8,
                    "application/json")
            };

            return Task.FromResult(response);
        }
    }
}

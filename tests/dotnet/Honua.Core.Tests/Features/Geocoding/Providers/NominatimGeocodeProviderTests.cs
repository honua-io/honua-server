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

    [Fact]
    public void Constructor_DoesNotMutateInjectedHttpClient_AllowsSharingAcrossProviders()
    {
        // A shared/pooled typed client whose headers are already set (simulating a prior request)
        // must not be mutated by the constructor; constructing a second provider over the same
        // client must not throw (DefaultRequestHeaders becomes read-only once a request starts).
        using var sharedClient = new HttpClient(new StubHttpMessageHandler())
        {
            BaseAddress = new Uri("https://example.com/", UriKind.Absolute)
        };
        sharedClient.DefaultRequestHeaders.UserAgent.ParseAdd("Existing/1.0");

        var configuration = new NominatimProviderConfiguration
        {
            BaseUrl = "https://example.com",
            UserAgent = "Honua.Tests/1.0",
            Email = "ops@example.com"
        };

        var first = new NominatimGeocodeProvider(configuration, sharedClient);
        var second = new NominatimGeocodeProvider(configuration, sharedClient);

        Assert.NotNull(first);
        Assert.NotNull(second);
        // The injected client's headers are untouched by construction.
        Assert.Equal("Existing/1.0", sharedClient.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public async Task ForwardGeocodeAsync_AppliesUserAgentAndContactHeaderPerRequest()
    {
        var handler = new HeaderCapturingHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.com/", UriKind.Absolute)
        };

        var provider = new NominatimGeocodeProvider(
            new NominatimProviderConfiguration
            {
                BaseUrl = "https://example.com",
                UserAgent = "Honua.Tests/1.0",
                Email = "ops@example.com"
            },
            httpClient);

        await provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("10 Downing St", 5, 4326, null),
            CancellationToken.None);

        Assert.Equal("Honua.Tests/1.0", handler.CapturedUserAgent);
        Assert.Equal("ops@example.com", handler.CapturedContactEmail);
    }

    // #2148: a forward-geocode proximity bias (location, no SearchBounds) is expressed as an
    // UNbounded viewbox centred on the bias point — results inside are preferred but outside
    // results are still returned (no &bounded=1).
    [Fact]
    public async Task ForwardGeocodeAsync_WithBiasLocation_EmitsUnboundedViewboxAroundBiasPoint()
    {
        var handler = new UriCapturingHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.com/", UriKind.Absolute)
        };

        var provider = new NominatimGeocodeProvider(
            new NominatimProviderConfiguration { BaseUrl = "https://example.com" },
            httpClient);

        await provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("cafe", 5, 4326, null)
            {
                BiasLocation = new GeocodePoint(-0.1276, 51.5034),
                BiasDistanceMeters = 1000
            },
            CancellationToken.None);

        Assert.NotNull(handler.CapturedUri);
        var query = handler.CapturedUri!.Query;
        Assert.Contains("viewbox=", query, StringComparison.Ordinal);
        // Proximity bias is a soft preference, so the bounded flag must NOT be present.
        Assert.DoesNotContain("bounded=1", query, StringComparison.Ordinal);
    }

    private sealed class UriCapturingHandler : HttpMessageHandler
    {
        public Uri? CapturedUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedUri = request.RequestUri;

            // Response ownership transfers to the caller via the return value
            // (HttpClient's pipeline disposes it); nothing leaks here.
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }

    private sealed class HeaderCapturingHandler : HttpMessageHandler
    {
        public string? CapturedUserAgent { get; private set; }

        public string? CapturedContactEmail { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedUserAgent = request.Headers.UserAgent.ToString();
            CapturedContactEmail = request.Headers.TryGetValues("X-Contact-Email", out var values)
                ? values.FirstOrDefault()
                : null;

            // Response ownership transfers to the caller via the return value
            // (HttpClient's pipeline disposes it); nothing leaks here.
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            // Response ownership transfers to the caller via the return value
            // (HttpClient's pipeline disposes it); nothing leaks here.
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
            // Response ownership transfers to the caller via the return value
            // (HttpClient's pipeline disposes it); nothing leaks here.
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

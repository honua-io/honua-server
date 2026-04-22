// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Core.Features.Geocoding.Domain;
using Honua.Core.Features.Geocoding.Providers;

namespace Honua.Core.Tests.Features.Geocoding.Providers;

public sealed class AzureMapsGeocodeProviderTests
{
    [Fact]
    public async Task ForwardGeocodeAsync_ValidResponse_ReturnsCandidate()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler())
        {
            BaseAddress = new Uri("https://example.com/", UriKind.Absolute)
        };

        var provider = CreateProvider(httpClient);

        var results = await provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("1 Microsoft Way", 5, 4326, null),
            CancellationToken.None);

        var candidate = Assert.Single(results);
        Assert.Equal("1 Microsoft Way, Redmond, WA", candidate.Address);
        Assert.Equal(-122.1298d, candidate.X, 4);
        Assert.Equal(47.6396d, candidate.Y, 4);
        Assert.Equal("azure-forward-1", candidate.ProviderId);
    }

    [Fact]
    public async Task ReverseGeocodeAsync_ValidResponse_ReturnsMatch()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler())
        {
            BaseAddress = new Uri("https://example.com/", UriKind.Absolute)
        };

        var provider = CreateProvider(httpClient);

        var result = await provider.ReverseGeocodeAsync(
            new ReverseGeocodeRequest(-122.1298d, 47.6396d, 4326),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("1 Microsoft Way, Redmond, WA", result.Address);
        Assert.Equal(-122.1298d, result.X, 4);
        Assert.Equal(47.6396d, result.Y, 4);
        Assert.Equal("azure-reverse-1", result.ProviderId);
    }

    [Fact]
    public async Task SuggestAsync_ValidResponse_ReturnsSuggestions()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler())
        {
            BaseAddress = new Uri("https://example.com/", UriKind.Absolute)
        };

        var provider = CreateProvider(httpClient);

        var results = await provider.SuggestAsync(
            new SuggestGeocodeRequest("microsoft", 5, null),
            CancellationToken.None);

        var suggestion = Assert.Single(results);
        Assert.Equal("Microsoft Visitor Center", suggestion.Text);
        Assert.Equal("azure-suggest-1", suggestion.MagicKey);
    }

    private static AzureMapsGeocodeProvider CreateProvider(HttpClient httpClient)
    {
        return new AzureMapsGeocodeProvider(
            new AzureMapsProviderConfiguration
            {
                BaseUrl = "https://example.com",
                SubscriptionKey = "test-key",
                TimeoutSeconds = 10,
                ApiVersion = "1.0",
                MaxResults = 10
            },
            httpClient);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = request.RequestUri?.AbsoluteUri switch
            {
                { } uri when uri.Contains("/search/address/reverse/json", StringComparison.Ordinal) =>
                    """
                    {"addresses":[{"type":"Point Address","id":"azure-reverse-1","entityType":"Address","matchCode":"Exact","address":{"streetNumber":"1","streetName":"Microsoft Way","municipality":"Redmond","countrySubdivision":"WA","postalCode":"98052","country":"United States","freeformAddress":"1 Microsoft Way, Redmond, WA"}}]}
                    """,
                { } uri when uri.Contains("typeahead=true", StringComparison.Ordinal) =>
                    """
                    {"results":[{"type":"POI","id":"azure-suggest-1","address":{"freeformAddress":"Microsoft Visitor Center"},"poi":{"name":"Microsoft Visitor Center"}}]}
                    """,
                _ =>
                    """
                    {"results":[{"type":"Point Address","id":"azure-forward-1","score":0.95,"entityType":"Address","position":{"lat":47.6396,"lon":-122.1298},"address":{"streetNumber":"1","streetName":"Microsoft Way","municipality":"Redmond","countrySubdivision":"WA","postalCode":"98052","country":"United States","freeformAddress":"1 Microsoft Way, Redmond, WA"},"matchCode":"Exact"}]}
                    """
            };

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }
}

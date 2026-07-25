// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geocoding.Features.Geocoding.Providers;

namespace Honua.Core.Tests.Features.Geocoding.Providers;

/// <summary>
/// Verifies structured-address input maps onto each provider's native request shape with parity, so
/// Azure Maps and Amazon Location honor the same structured fields Nominatim already did (#2149).
/// </summary>
public sealed class GeocodeStructuredInputTests
{
    private static readonly StructuredAddress Sample = new()
    {
        StreetName = "380 New York St",
        City = "Redlands",
        Region = "CA",
        PostalCode = "92373",
        Country = "US"
    };

    private static ForwardGeocodeRequest StructuredRequest() => new(
        Query: "380 New York St, Redlands, CA, 92373, US",
        MaxResults: 5,
        SpatialReferenceWkid: 4326,
        InputType: GeocodeInputType.Structured)
    {
        StructuredAddress = Sample
    };

    [Fact]
    public async Task Nominatim_StructuredInput_UsesStructuredQueryParameters()
    {
        var handler = new CapturingHandler("""[{"place_id":1,"lat":"34.0","lon":"-117.0","display_name":"380 New York St","address":{}}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };

        var provider = new NominatimGeocodeProvider(
            new NominatimProviderConfiguration { BaseUrl = "https://example.com" },
            httpClient);

        await provider.ForwardGeocodeAsync(StructuredRequest(), CancellationToken.None);

        var uri = handler.LastUri!.AbsoluteUri;
        Assert.Contains("street=380", uri, StringComparison.Ordinal);
        Assert.Contains("city=Redlands", uri, StringComparison.Ordinal);
        Assert.Contains("state=CA", uri, StringComparison.Ordinal);
        Assert.Contains("postalcode=92373", uri, StringComparison.Ordinal);
        Assert.Contains("country=US", uri, StringComparison.Ordinal);
        // A structured query must not also send the free-form q= parameter.
        Assert.DoesNotContain("&q=", uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureMaps_StructuredInput_UsesStructuredAddressEndpoint()
    {
        var handler = new CapturingHandler("""{"results":[]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };

        var provider = new AzureMapsGeocodeProvider(
            new AzureMapsProviderConfiguration { SubscriptionKey = "test-key", BaseUrl = "https://example.com" },
            httpClient);

        await provider.ForwardGeocodeAsync(StructuredRequest(), CancellationToken.None);

        var uri = handler.LastUri!.AbsoluteUri;
        Assert.Contains("/search/address/structured/json", uri, StringComparison.Ordinal);
        Assert.Contains("municipality=Redlands", uri, StringComparison.Ordinal);
        Assert.Contains("countrySubdivision=CA", uri, StringComparison.Ordinal);
        Assert.Contains("postalCode=92373", uri, StringComparison.Ordinal);
        Assert.Contains("countryCode=US", uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureMaps_SingleLineInput_UsesFreeformSearchEndpoint()
    {
        var handler = new CapturingHandler("""{"results":[]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };

        var provider = new AzureMapsGeocodeProvider(
            new AzureMapsProviderConfiguration { SubscriptionKey = "test-key", BaseUrl = "https://example.com" },
            httpClient);

        await provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("380 New York St, Redlands", 5, 4326),
            CancellationToken.None);

        var uri = handler.LastUri!.AbsoluteUri;
        Assert.Contains("/search/address/json", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("/structured/", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonLocation_StructuredInput_ComposesTextQuery()
    {
        var composed = AmazonLocationGeocodeProvider.ComposeStructuredText(Sample);

        Assert.Equal("380 New York St, Redlands, CA, 92373, US", composed);
    }

    [Fact]
    public void AmazonLocation_NoStructuredInput_ReturnsNull()
    {
        Assert.Null(AmazonLocationGeocodeProvider.ComposeStructuredText(null));
        Assert.Null(AmazonLocationGeocodeProvider.ComposeStructuredText(new StructuredAddress()));
    }

    private sealed class CapturingHandler(string payload) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        // Response ownership transfers to the caller via the return value
        // (HttpClient's pipeline disposes it); nothing leaks here.
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult<System.Net.Http.HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}

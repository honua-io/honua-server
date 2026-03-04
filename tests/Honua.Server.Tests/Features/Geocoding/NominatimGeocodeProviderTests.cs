// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Server.Features.Geocoding;
using Honua.Server.Features.Geocoding.Providers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Geocoding;

public sealed class NominatimGeocodeProviderTests
{
    [Fact]
    public async Task ForwardGeocodeAsync_MapsNominatimSearchResponse()
    {
        var handler = new StubHttpMessageHandler
        {
            ResponseFactory = request =>
            {
                LastRequestUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"place_id\":101,\"display_name\":\"10 Downing St, London\",\"lat\":\"51.5034\",\"lon\":\"-0.1276\",\"importance\":0.9,\"address\":{\"city\":\"London\"}}]",
                        Encoding.UTF8,
                        "application/json")
                };
            }
        };

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://nominatim.test/", UriKind.Absolute)
        };

        var provider = new NominatimGeocodeProvider(
            httpClient,
            Options.Create(new GeocodingOptions
            {
                Nominatim = new NominatimGeocodingOptions
                {
                    BaseUrl = "https://nominatim.test",
                    UserAgent = "Honua.Tests/1.0",
                    TimeoutSeconds = 10,
                    DefaultMaxResults = 10,
                    DefaultMaxSuggestions = 5,
                    EnableSuggestFromSearch = false
                }
            }),
            NullLogger<NominatimGeocodeProvider>.Instance);

        var results = await provider.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("10 Downing St", 5, 4326, null),
            CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("10 Downing St, London", results[0].Address);
        Assert.Equal(-0.1276d, results[0].X, 4);
        Assert.Equal(51.5034d, results[0].Y, 4);
        Assert.Equal("101", results[0].ProviderId);
        Assert.Equal("nominatim", results[0].Attributes["Provider"]);

        Assert.NotNull(LastRequestUri);
        Assert.Equal("/search", LastRequestUri!.AbsolutePath);
        var query = QueryHelpers.ParseQuery(LastRequestUri.Query);
        Assert.Equal("10 Downing St", query["q"].ToString());
        Assert.Equal("5", query["limit"].ToString());
    }

    [Fact]
    public void Capabilities_SuggestReflectsConfiguration()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler())
        {
            BaseAddress = new Uri("https://nominatim.test/", UriKind.Absolute)
        };

        var provider = new NominatimGeocodeProvider(
            httpClient,
            Options.Create(new GeocodingOptions
            {
                Nominatim = new NominatimGeocodingOptions
                {
                    BaseUrl = "https://nominatim.test",
                    UserAgent = "Honua.Tests/1.0",
                    TimeoutSeconds = 10,
                    EnableSuggestFromSearch = false
                }
            }),
            NullLogger<NominatimGeocodeProvider>.Instance);

        Assert.False(provider.Capabilities.SupportsSuggest);
    }

    private static Uri? LastRequestUri { get; set; }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = ResponseFactory?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }
}

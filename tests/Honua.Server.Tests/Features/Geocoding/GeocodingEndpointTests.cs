// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Server.Features.Geocoding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Geocoding;

public sealed class GeocodingEndpointTests
{
    [Fact]
    public async Task FindAddressCandidates_ReturnsGeoServicesPayload()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new GeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: false,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=1600+Pennsylvania+Ave+NW&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;

        Assert.True(root.TryGetProperty("candidates", out var candidates));
        Assert.Equal(JsonValueKind.Array, candidates.ValueKind);
        Assert.True(candidates.GetArrayLength() > 0);

        var firstCandidate = candidates[0];
        Assert.Equal("1600 Pennsylvania Ave NW", firstCandidate.GetProperty("address").GetString());
        Assert.True(firstCandidate.TryGetProperty("location", out _));
        Assert.True(firstCandidate.TryGetProperty("attributes", out _));
    }

    [Fact]
    public async Task GeocodeServerMetadata_ExposesProviderCapabilities()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new GeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: false,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var capabilities = payload.RootElement.GetProperty("capabilities").GetString();
        Assert.Equal("Geocode,ReverseGeocode,Suggest", capabilities);
    }

    [Fact]
    public async Task Suggest_ReturnsBadRequest_WhenProviderDoesNotSupportSuggest()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new GeocodeProviderCapabilities(
            SupportsSuggest: false,
            SupportsBatch: false,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/suggest?text=hon&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(FakeGeocodeProvider fakeProvider)
    {
        return new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Geocoding:Enabled"] = "true",
                    ["Geocoding:DefaultProvider"] = fakeProvider.Name,
                    ["Geocoding:LocatorName"] = "World",
                    ["Geocoding:DefaultSpatialReferenceWkid"] = "4326"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IGeocodeProvider>();
                services.RemoveAll<IGeocodeProviderResolver>();

                services.AddSingleton<IGeocodeProvider>(fakeProvider);
                services.AddScoped<IGeocodeProviderResolver, GeocodeProviderResolver>();
            });
        });
    }

    private sealed class FakeGeocodeProvider(GeocodeProviderCapabilities capabilities) : IGeocodeProvider
    {
        public string Name => "fake";

        public GeocodeProviderCapabilities Capabilities => capabilities;

        public Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(ForwardGeocodeRequest request, CancellationToken cancellationToken)
        {
            var candidate = new GeocodeCandidate(
                Address: "1600 Pennsylvania Ave NW",
                X: -77.03655,
                Y: 38.89768,
                Score: 99.1,
                Attributes: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Provider"] = Name,
                    ["Match_addr"] = "1600 Pennsylvania Ave NW"
                },
                ProviderId: "fake-1");

            return Task.FromResult<IReadOnlyList<GeocodeCandidate>>([candidate]);
        }

        public Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(ReverseGeocodeRequest request, CancellationToken cancellationToken)
        {
            var match = new ReverseGeocodeMatch(
                Address: "1600 Pennsylvania Ave NW",
                X: request.X,
                Y: request.Y,
                Attributes: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Provider"] = Name,
                    ["Match_addr"] = "1600 Pennsylvania Ave NW"
                },
                ProviderId: "fake-rev-1");

            return Task.FromResult<ReverseGeocodeMatch?>(match);
        }

        public Task<IReadOnlyList<GeocodeSuggestion>> SuggestAsync(SuggestGeocodeRequest request, CancellationToken cancellationToken)
        {
            var suggestion = new GeocodeSuggestion("Honua HQ", "fake-suggest-1", false);
            return Task.FromResult<IReadOnlyList<GeocodeSuggestion>>([suggestion]);
        }

        public Task<IReadOnlyList<GeocodeCandidate>> BatchGeocodeAsync(BatchGeocodeRequest request, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GeocodeCandidate>>([]);
    }
}

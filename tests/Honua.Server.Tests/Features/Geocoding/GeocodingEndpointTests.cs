// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Server.Features.Geocoding;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Geocoding;

[Protocol(Protocols.Geocoding)]
public sealed class GeocodingEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    [Endpoint("GET /rest/services/GeocodeServer/findAddressCandidates")]
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

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    [Endpoint("GET /rest/services/GeocodeServer")]
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

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/suggest")]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/suggest")]
    [Endpoint("GET /rest/services/GeocodeServer/suggest")]
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

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_ReturnsBadRequest_WhenProviderDoesNotSupportBatch()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new GeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: false,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/geocodeAddresses?f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    [Endpoint("GET /rest/services/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_ReturnsGeoServicesPayload()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new GeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: false,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/reverseGeocode?location=-77.03655,38.89768&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.Equal("1600 Pennsylvania Ave NW", root.GetProperty("address").GetProperty("Match_addr").GetString());
        Assert.Equal(-77.03655, root.GetProperty("location").GetProperty("x").GetDouble(), precision: 5);
        Assert.Equal(38.89768, root.GetProperty("location").GetProperty("y").GetDouble(), precision: 5);
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

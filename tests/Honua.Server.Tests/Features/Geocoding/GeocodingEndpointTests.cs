// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Geocoding;
using CoreGeocodeProvider = Honua.Core.Features.Geocoding.Abstractions.IGeocodeProvider;
using CoreGeocodeProviderCapabilities = Honua.Core.Features.Geocoding.Domain.GeocodeProviderCapabilities;
using CoreGeocodeProviderHealth = Honua.Core.Features.Geocoding.Domain.GeocodeProviderHealth;
using CoreGeocodeCandidate = Honua.Core.Features.Geocoding.Domain.GeocodeCandidate;
using CoreForwardGeocodeRequest = Honua.Core.Features.Geocoding.Domain.ForwardGeocodeRequest;
using CoreReverseGeocodeMatch = Honua.Core.Features.Geocoding.Domain.ReverseGeocodeMatch;
using CoreReverseGeocodeRequest = Honua.Core.Features.Geocoding.Domain.ReverseGeocodeRequest;
using CoreSuggestGeocodeRequest = Honua.Core.Features.Geocoding.Domain.SuggestGeocodeRequest;
using CoreBatchGeocodeRequest = Honua.Core.Features.Geocoding.Domain.BatchGeocodeRequest;
using CoreGeocodeSuggestion = Honua.Core.Features.Geocoding.Domain.GeocodeSuggestion;
using CoreGeocodeProviderException = Honua.Core.Features.Geocoding.Domain.GeocodeProviderException;
using Honua.Server.Features.Geocoding;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        using var factory = CreateDefaultFactory();
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
        using var factory = CreateDefaultFactory();
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
    public async Task Suggest_ReturnsBadRequest_WhenExplicitProviderDoesNotSupportSuggest()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: false,
            SupportsBatch: false,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/suggest?text=hon&provider=fake&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not supported", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_ReturnsBadRequest_WhenExplicitProviderDoesNotSupportBatch()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"test address"}}]""";
        using var response = await client.GetAsync($"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&provider=fake&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not supported", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    [Endpoint("GET /rest/services/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_ReturnsGeoServicesPayload()
    {
        using var factory = CreateDefaultFactory();
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

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/suggest")]
    public async Task Suggest_WhenProviderSupports_ReturnsSuggestions()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/suggest?text=hon&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.True(root.TryGetProperty("suggestions", out var suggestions));
        Assert.Equal(JsonValueKind.Array, suggestions.ValueKind);
        Assert.True(suggestions.GetArrayLength() > 0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    [Endpoint("GET /rest/services/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_HappyPath_ReturnsLocationsArray()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"1600 Pennsylvania Ave NW"}},{"attributes":{"SingleLine":"350 Fifth Avenue, New York"}}]""";

        using var response = await client.GetAsync($"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;

        Assert.True(root.TryGetProperty("locations", out var locations));
        Assert.Equal(JsonValueKind.Array, locations.ValueKind);
        Assert.Equal(2, locations.GetArrayLength());

        var firstLocation = locations[0];
        Assert.Equal("1600 Pennsylvania Ave NW", firstLocation.GetProperty("address").GetString());
        Assert.True(firstLocation.TryGetProperty("location", out _));
        Assert.True(firstLocation.TryGetProperty("score", out _));
        Assert.True(firstLocation.TryGetProperty("attributes", out _));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_Post_ReturnsLocationsArray()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"1600 Pennsylvania Ave NW"}}]""";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["records"] = records,
            ["f"] = "json"
        });

        using var response = await client.PostAsync("/rest/services/World/GeocodeServer/geocodeAddresses", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(payload.RootElement.TryGetProperty("locations", out var locations));
        Assert.Equal(1, locations.GetArrayLength());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_MissingRecords_Returns400()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/geocodeAddresses?f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_MalformedRecordsJson_Returns400()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/geocodeAddresses?records=not-valid-json&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid JSON", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_ExceedsMaxBatchSize_Returns400()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)
        { MaxBatchSize = 2 }));
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"addr1"}},{"attributes":{"SingleLine":"addr2"}},{"attributes":{"SingleLine":"addr3"}}]""";

        using var response = await client.GetAsync($"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&provider=fake&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("exceeds the maximum", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_InvalidRecordInBatch_Returns400WithIndex()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"valid address"}},{"attributes":{}},{"attributes":{"SingleLine":"another valid"}}]""";

        using var response = await client.GetAsync($"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("index 1", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithInvalidLocatorName_Returns404()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/NonexistentLocator/GeocodeServer/findAddressCandidates?singleLine=test&f=json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_Post_ReturnsGeoServicesPayload()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["singleLine"] = "1600 Pennsylvania Ave NW",
            ["f"] = "json"
        });

        using var response = await client.PostAsync("/rest/services/World/GeocodeServer/findAddressCandidates", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(payload.RootElement.TryGetProperty("candidates", out var candidates));
        Assert.True(candidates.GetArrayLength() > 0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_Post_ReturnsGeoServicesPayload()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["location"] = "-77.03655,38.89768",
            ["f"] = "json"
        });

        using var response = await client.PostAsync("/rest/services/World/GeocodeServer/reverseGeocode", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(payload.RootElement.TryGetProperty("address", out _));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_PjsonFormat_ReturnsJson()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test&f=pjson");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_InvalidFormat_Returns400()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test&f=xml");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    public async Task GeocodeServerMetadata_WhenGeocodingDisabled_Returns404()
    {
        using var factory = new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Geocoding:Enabled"] = "false"
                });
            });
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_MissingLocation_Returns400()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        // Missing location parameter
        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/reverseGeocode?f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task ForwardGeocode_ReturnsBadRequest_WhenExplicitProviderNotFound()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test&provider=nonexistent&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not found", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_ReturnsBadRequest_WhenExplicitProviderNotFound()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/reverseGeocode?location=-77.03,38.89&provider=nonexistent&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not found", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task ForwardGeocode_PrimaryFails_FallsBackToSecondary()
    {
        using var factory = CreateFailoverFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test+address&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var candidates = payload.RootElement.GetProperty("candidates");
        Assert.True(candidates.GetArrayLength() > 0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_PrimaryFails_FallsBackToSecondary()
    {
        using var factory = CreateFailoverFactory();
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"1600 Pennsylvania Ave NW"}}]""";

        using var response = await client.GetAsync($"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var locations = payload.RootElement.GetProperty("locations");
        Assert.Equal(1, locations.GetArrayLength());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task ForwardGeocode_AllProvidersFail_ReturnsError()
    {
        var failingProvider = new FailingGeocodeProvider("only-failing");
        // Override all auto-registered providers to ensure all providers fail
        var nominatimOverride = new FailingGeocodeProvider("nominatim");
        var azureOverride = new FailingGeocodeProvider("azuremaps");
        var amazonOverride = new FailingGeocodeProvider("amazonlocation");
        using var factory = new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Geocoding:Enabled"] = "true",
                    ["Geocoding:DefaultProvider"] = failingProvider.Name,
                    ["Geocoding:LocatorName"] = "World",
                    ["Geocoding:DefaultSpatialReferenceWkid"] = "4326",
                    ["Geocoding:EnableFailover"] = "true",
                    ["Geocoding:MaxFailoverAttempts"] = "10"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.AddGeocodeProvider(failingProvider.Name, _ => failingProvider, ServiceLifetime.Singleton);
                services.AddGeocodeProvider(nominatimOverride.Name, _ => nominatimOverride, ServiceLifetime.Singleton);
                services.AddGeocodeProvider(azureOverride.Name, _ => azureOverride, ServiceLifetime.Singleton);
                services.AddGeocodeProvider(amazonOverride.Name, _ => amazonOverride, ServiceLifetime.Singleton);
            });
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test&f=json");

        // The coordinator catches the exception and returns a failure result; handler maps to error
        Assert.True(
            response.StatusCode == HttpStatusCode.InternalServerError ||
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected error status code but got {response.StatusCode}");
    }

    private static readonly CoreGeocodeProviderCapabilities DefaultCapabilities = new(
        SupportsSuggest: true,
        SupportsBatch: false,
        SupportsStructuredInput: false,
        SupportsBiasing: true);

    private static WebApplicationFactory<Program> CreateDefaultFactory()
        => CreateFactory(new FakeGeocodeProvider(DefaultCapabilities));

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
                services.AddGeocodeProvider(fakeProvider.Name, _ => fakeProvider, ServiceLifetime.Singleton);
            });
        });
    }

    private static WebApplicationFactory<Program> CreateFailoverFactory()
    {
        var failingProvider = new FailingGeocodeProvider("failing-primary");
        var workingProvider = new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true));

        return new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Geocoding:Enabled"] = "true",
                    ["Geocoding:DefaultProvider"] = failingProvider.Name,
                    ["Geocoding:LocatorName"] = "World",
                    ["Geocoding:DefaultSpatialReferenceWkid"] = "4326",
                    ["Geocoding:EnableFailover"] = "true",
                    // High enough to exhaust all auto-registered providers before reaching fake
                    ["Geocoding:MaxFailoverAttempts"] = "10"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.AddGeocodeProvider(failingProvider.Name, _ => failingProvider, ServiceLifetime.Singleton);
                services.AddGeocodeProvider(workingProvider.Name, _ => workingProvider, ServiceLifetime.Singleton);
            });
        });
    }

    private sealed class FakeGeocodeProvider(CoreGeocodeProviderCapabilities capabilities) : CoreGeocodeProvider
    {
        public string Name => "fake";

        public CoreGeocodeProviderCapabilities Capabilities => capabilities;

        public Task<IReadOnlyList<CoreGeocodeCandidate>> ForwardGeocodeAsync(CoreForwardGeocodeRequest request, CancellationToken cancellationToken)
        {
            var candidate = new CoreGeocodeCandidate(
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

            return Task.FromResult<IReadOnlyList<CoreGeocodeCandidate>>([candidate]);
        }

        public Task<CoreReverseGeocodeMatch?> ReverseGeocodeAsync(CoreReverseGeocodeRequest request, CancellationToken cancellationToken)
        {
            var match = new CoreReverseGeocodeMatch(
                Address: "1600 Pennsylvania Ave NW",
                X: request.X,
                Y: request.Y,
                Attributes: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Provider"] = Name,
                    ["Match_addr"] = "1600 Pennsylvania Ave NW"
                },
                ProviderId: "fake-rev-1");

            return Task.FromResult<CoreReverseGeocodeMatch?>(match);
        }

        public Task<IReadOnlyList<CoreGeocodeSuggestion>> SuggestAsync(CoreSuggestGeocodeRequest request, CancellationToken cancellationToken)
        {
            var suggestion = new CoreGeocodeSuggestion("Honua HQ", "fake-suggest-1", false);
            return Task.FromResult<IReadOnlyList<CoreGeocodeSuggestion>>([suggestion]);
        }

        public Task<IReadOnlyList<CoreGeocodeCandidate>> BatchGeocodeAsync(CoreBatchGeocodeRequest request, CancellationToken cancellationToken)
        {
            var results = new List<CoreGeocodeCandidate>();
            for (int i = 0; i < request.Queries.Count; i++)
            {
                results.Add(new CoreGeocodeCandidate(
                    Address: request.Queries[i],
                    X: -77.03655 + i,
                    Y: 38.89768 + i,
                    Score: 95.0,
                    Attributes: new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["Provider"] = Name,
                        ["Match_addr"] = request.Queries[i]
                    },
                    ProviderId: $"fake-batch-{i}"));
            }

            return Task.FromResult<IReadOnlyList<CoreGeocodeCandidate>>(results);
        }

        public Task<CoreGeocodeProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CoreGeocodeProviderHealth(Name, true));
    }

    private sealed class FailingGeocodeProvider(string name) : CoreGeocodeProvider
    {
        private static readonly CoreGeocodeProviderCapabilities FailingCapabilities = new(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true);

        public string Name => name;

        public CoreGeocodeProviderCapabilities Capabilities => FailingCapabilities;

        public Task<IReadOnlyList<CoreGeocodeCandidate>> ForwardGeocodeAsync(CoreForwardGeocodeRequest request, CancellationToken cancellationToken)
            => throw new CoreGeocodeProviderException("Provider unavailable");

        public Task<CoreReverseGeocodeMatch?> ReverseGeocodeAsync(CoreReverseGeocodeRequest request, CancellationToken cancellationToken)
            => throw new CoreGeocodeProviderException("Provider unavailable");

        public Task<IReadOnlyList<CoreGeocodeSuggestion>> SuggestAsync(CoreSuggestGeocodeRequest request, CancellationToken cancellationToken)
            => throw new CoreGeocodeProviderException("Provider unavailable");

        public Task<IReadOnlyList<CoreGeocodeCandidate>> BatchGeocodeAsync(CoreBatchGeocodeRequest request, CancellationToken cancellationToken)
            => throw new CoreGeocodeProviderException("Provider unavailable");

        public Task<CoreGeocodeProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CoreGeocodeProviderHealth(Name, false));
    }
}

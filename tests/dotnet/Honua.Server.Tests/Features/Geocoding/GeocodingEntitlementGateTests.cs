// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Geocoding.Features.Geocoding;
using CoreGeocodeProvider = Honua.Geocoding.Features.Geocoding.Abstractions.IGeocodeProvider;
using CoreGeocodeProviderCapabilities = Honua.Geocoding.Features.Geocoding.Domain.GeocodeProviderCapabilities;
using CoreGeocodeProviderHealth = Honua.Geocoding.Features.Geocoding.Domain.GeocodeProviderHealth;
using CoreGeocodeCandidate = Honua.Geocoding.Features.Geocoding.Domain.GeocodeCandidate;
using CoreForwardGeocodeRequest = Honua.Geocoding.Features.Geocoding.Domain.ForwardGeocodeRequest;
using CoreReverseGeocodeMatch = Honua.Geocoding.Features.Geocoding.Domain.ReverseGeocodeMatch;
using CoreReverseGeocodeRequest = Honua.Geocoding.Features.Geocoding.Domain.ReverseGeocodeRequest;
using CoreSuggestGeocodeRequest = Honua.Geocoding.Features.Geocoding.Domain.SuggestGeocodeRequest;
using CoreBatchGeocodeRequest = Honua.Geocoding.Features.Geocoding.Domain.BatchGeocodeRequest;
using CoreGeocodeSuggestion = Honua.Geocoding.Features.Geocoding.Domain.GeocodeSuggestion;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Geocoding;

[Protocol(TestProtocols.Geocoding)]
public sealed class GeocodingEntitlementGateTests
{
    private static readonly CoreGeocodeProviderCapabilities BatchCapableCapabilities = new(
        SupportsSuggest: true,
        SupportsBatch: true,
        SupportsStructuredInput: false,
        SupportsBiasing: false);

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_WithoutEntitlement_Returns402WithEntitlementDetail()
    {
        using var factory = CreateFactory(devGrantEdition: null);
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"1600 Pennsylvania Ave NW"}}]""";
        using var response = await client.GetAsync(
            $"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&f=json");

        await response.AssertGeoServicesErrorAsync(402);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("geocoding.batch", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_Post_WithoutEntitlement_Returns402WithEntitlementDetail()
    {
        using var factory = CreateFactory(devGrantEdition: null);
        using var client = factory.CreateClient();

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["records"] = """[{"attributes":{"SingleLine":"1600 Pennsylvania Ave NW"}}]""",
            ["f"] = "json"
        });
        using var response = await client.PostAsync("/rest/services/World/GeocodeServer/geocodeAddresses", content);

        await response.AssertGeoServicesErrorAsync(402);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("geocoding.batch", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_Alias_WithoutEntitlement_Returns402()
    {
        using var factory = CreateFactory(devGrantEdition: null);
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"1600 Pennsylvania Ave NW"}}]""";
        using var response = await client.GetAsync(
            $"/rest/services/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&f=json");

        await response.AssertGeoServicesErrorAsync(402);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_WithProEntitlement_StillReturns402()
    {
        // Batch is Enterprise (ADR-0024's volume/enterprise geocoding workload), not Pro: the
        // "Pro-is-not-enough" shape applies here too.
        using var factory = CreateFactory(devGrantEdition: "Pro");
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"1600 Pennsylvania Ave NW"}}]""";
        using var response = await client.GetAsync(
            $"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&f=json");

        await response.AssertGeoServicesErrorAsync(402);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_WithEnterpriseEntitlement_ReachesEndpoint()
    {
        using var factory = CreateFactory(devGrantEdition: "Enterprise");
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"1600 Pennsylvania Ave NW"}}]""";
        using var response = await client.GetAsync(
            $"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(payload.RootElement.TryGetProperty("locations", out var locations));
        Assert.Equal(1, locations.GetArrayLength());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task ForwardGeocode_WithoutAnyLicense_Succeeds()
    {
        // Forward geocoding is Community now — no license/entitlement service configured
        // at all must still reach the endpoint.
        using var factory = CreateFactory(devGrantEdition: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=1600+Pennsylvania+Ave+NW&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(payload.RootElement.GetProperty("candidates").GetArrayLength() > 0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_WithoutAnyLicense_Succeeds()
    {
        using var factory = CreateFactory(devGrantEdition: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/reverseGeocode?location=-77.03655,38.89768&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(payload.RootElement.TryGetProperty("address", out _));
    }

    [IntegrationTheory]
    [InlineData(null, false)]
    [InlineData("Enterprise", true)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    public async Task GeocodeServerMetadata_AnonymousCaller_AdvertisesBatchOnlyWithEntitlement(
        string? devGrantEdition,
        bool supportsBatch)
    {
        using var factory = CreateFactory(devGrantEdition);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var metadata = payload.RootElement;
        var capabilities = metadata.GetProperty("capabilities").GetString()!.Split(',');
        var locatorProperties = metadata.GetProperty("locatorProperties");
        Assert.Contains("Suggest", capabilities);
        Assert.DoesNotContain("BatchGeocode", capabilities);
        Assert.Equal(supportsBatch ? "true" : "false", locatorProperties.GetProperty("SupportsBatch").GetString());
        Assert.Equal(supportsBatch, locatorProperties.TryGetProperty("MaxBatchSize", out _));
        var hasSuggestedBatchSize = locatorProperties.TryGetProperty("SuggestedBatchSize", out var suggestedBatchSize);
        Assert.Equal(supportsBatch, hasSuggestedBatchSize);
        if (hasSuggestedBatchSize)
        {
            Assert.Equal(100, suggestedBatchSize.GetInt32());
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string? devGrantEdition)
    {
        var provider = new FakeGeocodeProvider(BatchCapableCapabilities);

        return new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Geocoding:Enabled"] = "true",
                    ["Geocoding:DefaultProvider"] = provider.Name,
                    ["Geocoding:LocatorName"] = "World",
                    ["Geocoding:DefaultSpatialReferenceWkid"] = "4326"
                });
            });

            // Licensing:DevGrantEdition is read by AddHonuaLicensing while the minimal-hosting-model
            // WebApplicationBuilder is still being configured, before a ConfigureAppConfiguration
            // callback added here would apply. UseSetting (mirrors the
            // IdentityEntitlementGateTests pattern) pushes it in early enough to be honored.
            if (devGrantEdition is not null)
            {
                builder.UseSetting("Licensing:DevGrantEdition", devGrantEdition);
            }

            builder.ConfigureServices(services =>
            {
                services.AddGeocodeProvider(provider.Name, _ => provider, ServiceLifetime.Singleton);
            });
        });
    }

    private sealed class FakeGeocodeProvider(CoreGeocodeProviderCapabilities capabilities) : CoreGeocodeProvider
    {
        public string Name => "fake-gate";

        public CoreGeocodeProviderCapabilities Capabilities => capabilities;

        public Task<IReadOnlyList<CoreGeocodeCandidate>> ForwardGeocodeAsync(CoreForwardGeocodeRequest request, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CoreGeocodeCandidate>>(
            [
                new CoreGeocodeCandidate(
                    Address: "1600 Pennsylvania Ave NW",
                    X: -77.03655,
                    Y: 38.89768,
                    Score: 99.1,
                    Attributes: new Dictionary<string, string?>(StringComparer.Ordinal) { ["Provider"] = Name },
                    ProviderId: "fake-gate-1")
            ]);

        public Task<CoreReverseGeocodeMatch?> ReverseGeocodeAsync(CoreReverseGeocodeRequest request, CancellationToken cancellationToken)
            => Task.FromResult<CoreReverseGeocodeMatch?>(new CoreReverseGeocodeMatch(
                Address: "1600 Pennsylvania Ave NW",
                X: request.X,
                Y: request.Y,
                Attributes: new Dictionary<string, string?>(StringComparer.Ordinal) { ["Provider"] = Name },
                ProviderId: "fake-gate-rev-1"));

        public Task<IReadOnlyList<CoreGeocodeSuggestion>> SuggestAsync(CoreSuggestGeocodeRequest request, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CoreGeocodeSuggestion>>(
                [new CoreGeocodeSuggestion("1600 Pennsylvania Ave NW", "fake-gate-suggest-1", false)]);

        public Task<IReadOnlyList<CoreGeocodeCandidate>> BatchGeocodeAsync(CoreBatchGeocodeRequest request, CancellationToken cancellationToken)
        {
            var results = new List<CoreGeocodeCandidate>();
            for (var i = 0; i < request.Queries.Count; i++)
            {
                results.Add(new CoreGeocodeCandidate(
                    Address: request.Queries[i],
                    X: -77.03655 + i,
                    Y: 38.89768 + i,
                    Score: 95.0,
                    Attributes: new Dictionary<string, string?>(StringComparer.Ordinal) { ["Provider"] = Name },
                    ProviderId: $"fake-gate-batch-{i}"));
            }

            return Task.FromResult<IReadOnlyList<CoreGeocodeCandidate>>(results);
        }

        public Task<CoreGeocodeProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CoreGeocodeProviderHealth(Name, true));
    }
}

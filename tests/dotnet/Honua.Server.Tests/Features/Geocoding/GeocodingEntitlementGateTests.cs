// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Entitlement-gate tests for the GeoServices GeocodeServer HTTP surface (#2981). Before this
/// change, the catalog declared <c>geocoding.forward</c>/<c>geocoding.reverse</c> Pro and
/// <c>geocoding.batch</c> Enterprise, but no HTTP endpoint enforced any of them — the release
/// owner's "split" decision re-tiers forward/reverse to Community (the demo/adoption showcase
/// path) and adds real enforcement for batch geocoding (the volume/enterprise workload) via the
/// same <c>LicenseGate.RequireEntitlement</c> endpoint-filter pattern used for SAML/SCIM (#2978).
///
/// These tests prove both directions: <c>geocodeAddresses</c> signals the entitlement failure
/// below Enterprise (including the #2978 "Pro-is-not-enough" shape) and succeeds at Enterprise;
/// <c>findAddressCandidates</c>/<c>reverseGeocode</c> succeed with no license configured at all,
/// proving they carry no entitlement gate. GeocodeServer metadata stays reachable with no license
/// either, per the issue's "metadata/discovery stays Community" requirement.
///
/// The GeoServices REST surface (PA-070/PA-117, #2418) signals ALL errors — including this
/// entitlement gate — with HTTP <b>200</b> and an <c>{"error":{"code":402,...}}</c> body rather
/// than a raw non-2xx status, matching every Esri client's error-handling contract. These tests
/// therefore assert via <see cref="GeoServicesErrorAssertions.AssertGeoServicesErrorAsync(HttpResponseMessage, int, bool)"/>
/// (body <c>error.code == 402</c>) rather than <see cref="HttpStatusCode.PaymentRequired"/>
/// directly — unlike the #2978 SAML/SCIM gate, which sits outside the GeoServices path and
/// returns a literal HTTP 402.
/// </summary>
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
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_WithProEntitlement_StillReturns402()
    {
        // Batch is Enterprise (ADR-0024's volume/enterprise geocoding workload), not Pro: the
        // #2978 "Pro-is-not-enough" shape applies here too.
        using var factory = CreateFactory(devGrantEdition: "Pro");
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"1600 Pennsylvania Ave NW"}}]""";
        using var response = await client.GetAsync(
            $"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&f=json");

        await response.AssertGeoServicesErrorAsync(402);
    }

    [IntegrationTest]
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
        // #2981: forward geocoding is Community now — no license/entitlement service configured
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

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    public async Task GeocodeServerMetadata_WithoutAnyLicense_Succeeds()
    {
        // GeocodeServer metadata/discovery must stay Community regardless of the batch gate.
        using var factory = CreateFactory(devGrantEdition: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(string? devGrantEdition)
    {
        var provider = new FakeGeocodeProvider(BatchCapableCapabilities);

        return new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
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
            // callback added here would apply. UseSetting (mirrors the #2978
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

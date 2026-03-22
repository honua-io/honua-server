// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for platform admin endpoints: license, identity, cache, geocoding, features.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
public sealed class PlatformAdminEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // --- License Status ---

    [IntegrationTest]
    [Operation(Operations.License)]
    [Endpoint("GET /api/v1/admin/license/status")]
    public async Task GetLicenseStatus_ReturnsValidResponse()
    {
        var response = await _client.GetAsync("/api/v1/admin/license/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = document.RootElement.GetProperty("data");
        data.GetProperty("edition").GetString().Should().NotBeNullOrEmpty();
        data.GetProperty("isValid").GetBoolean().Should().BeTrue();
    }

    // --- License Entitlements ---

    [IntegrationTest]
    [Operation(Operations.License)]
    [Endpoint("GET /api/v1/admin/license/features")]
    public async Task GetLicenseEntitlements_ReturnsFeatureList()
    {
        var response = await _client.GetAsync("/api/v1/admin/license/features");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = document.RootElement.GetProperty("data");
        data.GetProperty("edition").GetString().Should().NotBeNullOrEmpty();

        var features = data.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);

        // Each feature should have required properties
        var firstFeature = features[0];
        firstFeature.GetProperty("key").GetString().Should().NotBeNullOrEmpty();
        firstFeature.GetProperty("displayName").GetString().Should().NotBeNullOrEmpty();
        firstFeature.GetProperty("category").GetString().Should().NotBeNullOrEmpty();
        firstFeature.GetProperty("minimumEdition").GetString().Should().NotBeNullOrEmpty();
        firstFeature.TryGetProperty("isEnabled", out _).Should().BeTrue();
        firstFeature.TryGetProperty("upgradeRequired", out _).Should().BeTrue();
    }

    // --- License Upload ---

    [IntegrationTest]
    [Operation(Operations.License)]
    [Endpoint("POST /api/v1/admin/license/upload")]
    public async Task UploadLicense_Returns501NotImplemented()
    {
        var content = new StringContent("fake-license-data");
        var response = await _client.PostAsync("/api/v1/admin/license/upload", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("success").GetBoolean().Should().BeFalse();
        data.GetProperty("message").GetString().Should().Contain("not yet supported");
    }

    // --- Identity Providers ---

    [IntegrationTest]
    [Operation(Operations.Identity)]
    [Endpoint("GET /api/v1/admin/identity/providers")]
    public async Task GetIdentityProviders_ReturnsProviderList()
    {
        var response = await _client.GetAsync("/api/v1/admin/identity/providers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = document.RootElement.GetProperty("data");
        data.TryGetProperty("enabled", out _).Should().BeTrue();
        data.TryGetProperty("providers", out _).Should().BeTrue();
    }

    // --- Identity Provider Test ---

    [IntegrationTest]
    [Operation(Operations.Identity)]
    [Endpoint("GET /api/v1/admin/identity/providers/{providerType}/test")]
    public async Task TestIdentityProvider_UnknownProvider_ReturnsUnreachable()
    {
        var response = await _client.GetAsync("/api/v1/admin/identity/providers/UnknownProvider/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = document.RootElement.GetProperty("data");
        data.GetProperty("providerType").GetString().Should().Be("UnknownProvider");
        data.GetProperty("isReachable").GetBoolean().Should().BeFalse();
        data.GetProperty("errorMessage").GetString().Should().Contain("not configured");
    }

    // --- Cache Status ---

    [IntegrationTest]
    [Operation(Operations.Cache)]
    [Endpoint("GET /api/v1/admin/cache/status")]
    public async Task GetCacheStatus_ReturnsHealthInfo()
    {
        var response = await _client.GetAsync("/api/v1/admin/cache/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = document.RootElement.GetProperty("data");
        data.TryGetProperty("isHealthy", out _).Should().BeTrue();
        data.TryGetProperty("isUsingFallback", out _).Should().BeTrue();
        data.TryGetProperty("timestamp", out _).Should().BeTrue();
    }

    // --- Cache Invalidation ---

    [IntegrationTest]
    [Operation(Operations.Cache)]
    [Endpoint("POST /api/v1/admin/cache/invalidate")]
    public async Task InvalidateCache_OgcMetadataScope_ReturnsSuccess()
    {
        var payload = new { scope = "ogc-metadata" };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/cache/invalidate", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = document.RootElement.GetProperty("data");
        data.GetProperty("success").GetBoolean().Should().BeTrue();
        data.GetProperty("scope").GetString().Should().Be("ogc-metadata");
    }

    [IntegrationTest]
    [Operation(Operations.Cache)]
    [Endpoint("POST /api/v1/admin/cache/invalidate")]
    public async Task InvalidateCache_AllScope_ReturnsSuccess()
    {
        var payload = new { scope = "all" };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/cache/invalidate", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Cache)]
    [Endpoint("POST /api/v1/admin/cache/invalidate")]
    public async Task InvalidateCache_LayerScope_WithoutIds_Returns400()
    {
        var payload = new { scope = "layer" };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/cache/invalidate", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Cache)]
    [Endpoint("POST /api/v1/admin/cache/invalidate")]
    public async Task InvalidateCache_ServiceScope_WithoutServiceId_Returns400()
    {
        var payload = new { scope = "service" };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/cache/invalidate", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Cache)]
    [Endpoint("POST /api/v1/admin/cache/invalidate")]
    public async Task InvalidateCache_InvalidScope_Returns400()
    {
        var payload = new { scope = "invalid-scope" };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/cache/invalidate", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Geocoding Providers ---

    [IntegrationTest]
    [Operation(Operations.GeocodingAdmin)]
    [Endpoint("GET /api/v1/admin/geocoding/providers")]
    public async Task GetGeocodingProviders_ReturnsProviderList()
    {
        var response = await _client.GetAsync("/api/v1/admin/geocoding/providers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = document.RootElement.GetProperty("data");
        data.TryGetProperty("failoverEnabled", out _).Should().BeTrue();
        data.TryGetProperty("providers", out _).Should().BeTrue();
    }

    // --- Feature Overview ---

    [IntegrationTest]
    [Operation(Operations.Features)]
    [Endpoint("GET /api/v1/admin/features")]
    public async Task GetFeatureOverview_ReturnsEditionAndFeatures()
    {
        var response = await _client.GetAsync("/api/v1/admin/features");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = document.RootElement.GetProperty("data");
        data.GetProperty("currentEdition").GetString().Should().NotBeNullOrEmpty();

        var features = data.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);

        // Each feature should have the expected structure
        var firstFeature = features[0];
        firstFeature.GetProperty("key").GetString().Should().NotBeNullOrEmpty();
        firstFeature.GetProperty("displayName").GetString().Should().NotBeNullOrEmpty();
        firstFeature.GetProperty("category").GetString().Should().NotBeNullOrEmpty();
        firstFeature.GetProperty("description").GetString().Should().NotBeNullOrEmpty();
        firstFeature.TryGetProperty("isEnabled", out _).Should().BeTrue();
        firstFeature.GetProperty("minimumEdition").GetString().Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Features)]
    [Endpoint("GET /api/v1/admin/features")]
    public async Task GetFeatureOverview_ProEdition_ShowsUpgradeMessagesForEnterprise()
    {
        var response = await _client.GetAsync("/api/v1/admin/features");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        var features = data.GetProperty("features");

        // Find an Enterprise-only feature - it should show upgrade message since default config is Pro
        var hasEnterpriseFeature = false;
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.GetProperty("minimumEdition").GetString() == "Enterprise")
            {
                hasEnterpriseFeature = true;
                feature.GetProperty("isEnabled").GetBoolean().Should().BeFalse();
                feature.GetProperty("upgradeMessage").GetString().Should().Contain("Requires Enterprise");
                break;
            }
        }

        hasEnterpriseFeature.Should().BeTrue("the feature catalog should contain Enterprise features");
    }
}

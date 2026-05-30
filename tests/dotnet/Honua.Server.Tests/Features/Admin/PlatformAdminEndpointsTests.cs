// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for platform admin endpoints: license, identity, cache, geocoding, features.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
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

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("DELETE /api/v1/admin/performance/enhanced/cache/invalidate")]
    public async Task InvalidateEnhancedPerformanceCache_WithoutPattern_Returns400()
    {
        var response = await _client.DeleteAsync("/api/v1/admin/performance/enhanced/cache/invalidate");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Performance)]
    [Endpoint("GET /api/v1/admin/performance/enhanced/database/query-performance")]
    [Endpoint("GET /api/v1/admin/performance/enhanced/database/slow-queries")]
    [Endpoint("GET /api/v1/admin/performance/enhanced/resources/tracking")]
    [Endpoint("GET /api/v1/admin/performance/enhanced/resources/potential-leaks")]
    [Endpoint("GET /api/v1/admin/performance/enhanced/exceptions/statistics")]
    [Endpoint("GET /api/v1/admin/performance/enhanced/exceptions/recent")]
    [Endpoint("GET /api/v1/admin/performance/enhanced/cache/statistics")]
    [Endpoint("GET /api/v1/admin/performance/enhanced/cache/effectiveness")]
    [Endpoint("GET /api/v1/admin/performance/enhanced/summary")]
    public async Task GetEnhancedPerformanceEndpoints_ReturnOk()
    {
        var endpoints = new[]
        {
            "/api/v1/admin/performance/enhanced/database/query-performance",
            "/api/v1/admin/performance/enhanced/database/slow-queries",
            "/api/v1/admin/performance/enhanced/resources/tracking",
            "/api/v1/admin/performance/enhanced/resources/potential-leaks",
            "/api/v1/admin/performance/enhanced/exceptions/statistics",
            "/api/v1/admin/performance/enhanced/exceptions/recent",
            "/api/v1/admin/performance/enhanced/cache/statistics",
            "/api/v1/admin/performance/enhanced/cache/effectiveness",
            "/api/v1/admin/performance/enhanced/summary"
        };

        foreach (var endpoint in endpoints)
        {
            var response = await _client.GetAsync(endpoint);
            var responseBody = await response.Content.ReadAsStringAsync();
            var allowedMethods = response.Headers.TryGetValues("Allow", out var headerValues)
                ? string.Join(',', headerValues)
                : "<none>";
            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "enhanced performance endpoint {0} should support GET (Allow: {1}, Body: {2})",
                endpoint,
                allowedMethods,
                responseBody);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Performance)]
    [Endpoint("POST /api/v1/admin/performance/enhanced/resources/scan-leaks")]
    public async Task ScanEnhancedPerformanceResourceLeaks_ReturnsOk()
    {
        var response = await _client.PostAsync("/api/v1/admin/performance/enhanced/resources/scan-leaks", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- License Upload ---

    [IntegrationTest]
    [Operation(Operations.License)]
    [Endpoint("POST /api/v1/admin/license/upload")]
    public async Task UploadLicense_WhenAdminUploadDisabled_Returns400()
    {
        var content = new StringContent("fake-license-data");
        var response = await _client.PostAsync("/api/v1/admin/license/upload", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();

        var data = document.RootElement.GetProperty("data");
        data.GetProperty("success").GetBoolean().Should().BeFalse();
        data.GetProperty("message").GetString().Should().Contain("License upload is disabled");
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

    [UnitTest]
    [Operation(Operations.Identity)]
    public async Task TestIdentityProvider_WhenDiscoveryFails_ReturnsSanitizedError()
    {
        var method = typeof(IdentityAdminEndpoints).GetMethod(
            "HandleTestProvider",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        using var httpClientFactory = new ThrowingHttpClientFactory(new HttpRequestException("dns failure"));
        var resultTask = method!.Invoke(
            null,
            [
                "Generic",
                Options.Create(new OidcAuthenticationOptions
                {
                    Enabled = true,
                    Generic = new GenericOidcProviderOptions
                    {
                        Enabled = true,
                        Authority = "https://auth.example.com",
                        ClientId = "generic-client-id",
                        ClientSecret = "generic-secret-value-minimum-length",
                        DisplayName = "External Provider"
                    }
                }),
                httpClientFactory,
                NullLogger<IdentityAdminEndpoints.IdentityAdminEndpointsLog>.Instance,
                CancellationToken.None
            ]) as Task<IResult>;

        resultTask.Should().NotBeNull();
        var result = await resultTask!;

        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("isReachable").GetBoolean().Should().BeFalse();
        data.GetProperty("errorMessage").GetString().Should().Be("Identity provider discovery request failed.");
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

    private sealed class ThrowingHttpClientFactory(Exception exception) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new ThrowingHttpMessageHandler(exception));

        public HttpClient CreateClient(string name) => _client;

        public void Dispose()
        {
            _client.Dispose();
        }

        private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromException<HttpResponseMessage>(exception);
        }
    }
}

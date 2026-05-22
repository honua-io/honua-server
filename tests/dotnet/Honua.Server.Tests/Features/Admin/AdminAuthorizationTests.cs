// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Verifies that every admin endpoint group rejects unauthenticated requests with 401
/// when dev-auth bypass is disabled.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AdminAuthorizationTests : IAsyncLifetime
{
    private const string AdminPassword = "admin-auth-test-key";
    private readonly WebAppFixture _fixture;
    private HttpClient _unauthenticatedClient = null!;

    public AdminAuthorizationTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _unauthenticatedClient = _fixture.CreateClient(); // No admin headers
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // --- AdminEndpoints (config, openapi, version, capabilities, tables) ---
    // v1 manifest endpoints were removed in #1035 cutover; their auth tests removed with them.

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/config")]
    public async Task GetConfig_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/config");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/openapi.json")]
    public async Task GetOpenApi_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/openapi.json");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/version")]
    public async Task GetVersion_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/version");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/capabilities")]
    public async Task GetCapabilities_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/capabilities");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- ServiceSettingsEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services")]
    public async Task GetServices_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/services");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/settings")]
    public async Task GetServiceSettings_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/services/test-svc/settings");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    public async Task PutServiceProtocols_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PutAsJsonAsync("/api/v1/admin/services/test-svc/protocols", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/access-policy")]
    public async Task PutServiceAccessPolicy_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PutAsJsonAsync("/api/v1/admin/services/test-svc/access-policy", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- ObservabilityEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/errors")]
    public async Task GetRecentErrors_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/observability/errors");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/telemetry")]
    public async Task GetTelemetryStatus_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/observability/telemetry");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/migrations")]
    public async Task GetMigrationStatus_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/observability/migrations");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- DeployControlEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/deploy/preflight")]
    public async Task GetDeployPreflight_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/deploy/preflight");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/plan")]
    public async Task PostDeployPlan_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsJsonAsync("/api/v1/admin/deploy/plan", new { targetId = "x" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations")]
    public async Task PostDeployOperations_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsJsonAsync("/api/v1/admin/deploy/operations", new { targetId = "x" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- AlertAdminEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/alerts/zones")]
    public async Task GetAlertZones_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/alerts/zones");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/zones")]
    public async Task PostAlertZone_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsJsonAsync("/api/v1/admin/alerts/zones", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/alerts/rules")]
    public async Task GetAlertRules_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/alerts/rules");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    public async Task PostAlertRule_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsJsonAsync("/api/v1/admin/alerts/rules", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- LayerPublishingEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/layers")]
    public async Task GetPublishedLayers_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync($"/api/v1/admin/connections/{Guid.NewGuid()}/layers");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PostPublishLayer_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsJsonAsync($"/api/v1/admin/connections/{Guid.NewGuid()}/layers", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- AdminLayerStyleEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task GetLayerStyle_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/metadata/layers/1/style");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task PutLayerStyle_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PutAsJsonAsync("/api/v1/admin/metadata/layers/1/style", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/fields")]
    public async Task GetLayerFields_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/metadata/layers/1/fields");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/fields")]
    public async Task PutLayerFields_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PutAsJsonAsync("/api/v1/admin/metadata/layers/1/fields", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/filter")]
    public async Task GetLayerFilter_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/metadata/layers/1/filter");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/filter")]
    public async Task PutLayerFilter_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PutAsJsonAsync("/api/v1/admin/metadata/layers/1/filter", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- MetadataResourceEndpoints removed in #1035 cutover; auth tests removed with them. ---

    // --- OperationsProgressEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/{operationId}")]
    public async Task GetOperationStatus_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/operations/test-op-id");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/active")]
    public async Task GetActiveOperations_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/operations/active");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- FeatureChangeEventsEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/feature-events/replay")]
    public async Task GetFeatureEventsReplay_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/feature-events/replay");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- TileOperationsEndpoints ---

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    public async Task PostTileJob_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsJsonAsync("/api/v1/admin/tile-operations/jobs", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/tile-operations/jobs")]
    public async Task GetTileJobs_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/tile-operations/jobs");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- SecureConnectionEndpoints (additional auth coverage beyond existing tests) ---

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/encryption/validate")]
    public async Task PostEncryptionValidate_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsync("/api/v1/admin/connections/encryption/validate", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/encryption/rotate-key")]
    public async Task PostEncryptionRotateKey_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsync("/api/v1/admin/connections/encryption/rotate-key", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- ComplianceAdminEndpoints (#352) ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/compliance/dashboard")]
    public async Task GetComplianceDashboard_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/compliance/dashboard");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/compliance/report")]
    public async Task GetComplianceReport_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/compliance/report?format=csv");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/compliance/residency/evaluate")]
    public async Task PostComplianceResidencyEvaluate_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsJsonAsync(
            "/api/v1/admin/compliance/residency/evaluate",
            new { region = "us-east-1" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/compliance/encryption/rotate-key")]
    public async Task PostComplianceRotateKey_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsync("/api/v1/admin/compliance/encryption/rotate-key", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- LicenseAdminEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/license/status")]
    public async Task GetLicenseStatus_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/license/status");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/license/features")]
    public async Task GetLicenseEntitlements_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/license/features");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/license/upload")]
    public async Task PostLicenseUpload_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsync("/api/v1/admin/license/upload", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- CacheOperationsEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/cache/health")]
    public async Task GetCacheOperationsHealth_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/operations/cache/health");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- IdentityAdminEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/identity/providers")]
    public async Task GetIdentityProviders_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/identity/providers");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/identity/providers/{providerType}/test")]
    public async Task TestIdentityProvider_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/identity/providers/AzureAd/test");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- CacheAdminEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/cache/status")]
    public async Task GetCacheStatus_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/cache/status");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/cache/invalidate")]
    public async Task PostCacheInvalidate_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsJsonAsync("/api/v1/admin/cache/invalidate", new { scope = "all" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/cache/invalidate")]
    public async Task PostCacheOperationsInvalidate_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.PostAsJsonAsync("/api/v1/admin/operations/cache/invalidate", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- StreamingOperationsEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/streaming/subscribers")]
    public async Task GetStreamingOperationsSubscribers_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/operations/streaming/subscribers");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/streaming/alerts")]
    public async Task GetStreamingOperationsAlerts_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/operations/streaming/alerts");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/operations/streaming/subscribers/{subscriberId}")]
    public async Task DeleteStreamingOperationsSubscriber_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.DeleteAsync(
            $"/api/v1/admin/operations/streaming/subscribers/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- GeocodingAdminEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/geocoding/providers")]
    public async Task GetGeocodingProviders_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/geocoding/providers");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- GeocodingOperationsEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/geocoding/providers")]
    public async Task GetGeocodingOperationsProviders_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/operations/geocoding/providers");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/geocoding/configuration")]
    public async Task GetGeocodingOperationsConfiguration_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/operations/geocoding/configuration");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- FeatureOverviewEndpoints ---

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/features")]
    public async Task GetFeatureOverview_WithoutAuth_Returns401()
    {
        var response = await _unauthenticatedClient.GetAsync("/api/v1/admin/features");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

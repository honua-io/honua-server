// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Models;
using Honua.TestKit.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for license management admin endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public class LicenseEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "license-admin-key";
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public LicenseEndpointsTests()
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
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/license")]
    public async Task GetLicenseStatus_WithAdminAuth_ReturnsLicenseInfo()
    {
        var response = await _client.GetAsync("/api/v1/admin/license");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<LicenseStatusResponse>>(json, _jsonOptions);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("Community", result.Data.Edition);
        Assert.Equal("NoLicenseConfigured", result.Data.ValidationState);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/license")]
    public async Task GetLicenseStatus_WithSignedStartupLicense_ReturnsLicenseIdentityAndEntitlements()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var licensePath = Path.Combine(tempDirectory.FullName, "license.honua-license.json");
        var license = LicenseTestSupport.CreateSignedLicense(
            HonuaEdition.Pro,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            entitlements: ["analytics.clustering", "staticmap.high-dpi"],
            capacity: new LicenseCapacityTerms
            {
                MaxSustainedServingUnits = 4m,
                AnnualSurgeDays = 14,
                SurgeAllowance = LicenseCapacitySurgeAllowances.Standard
            });
        await File.WriteAllBytesAsync(licensePath, license.LicenseData);

        var fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Licensing:LicensePath"] = licensePath,
                        [$"Licensing:TrustedKeys:{LicenseTestSupport.KeyId}"] = license.PublicKeySetting
                    });
                });
            });

        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            var response = await client.GetAsync("/api/v1/admin/license");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ApiResponse<LicenseStatusResponse>>(json, _jsonOptions);

            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.Equal("Pro", result.Data.Edition);
            Assert.Equal("Valid", result.Data.ValidationState);
            Assert.Equal("lic-test-338", result.Data.LicenseId);
            Assert.Equal("Honua Test Operator", result.Data.LicensedTo);
            Assert.Contains(result.Data.Entitlements, entitlement =>
                entitlement.Key == "analytics.clustering" && entitlement.IsActive);
            Assert.Contains(result.Data.Entitlements, entitlement =>
                entitlement.Key == "analytics.spatial-join" && !entitlement.IsActive);
            Assert.NotNull(result.Data.Capacity);
            Assert.Equal(4m, result.Data.Capacity.Terms?.MaxSustainedServingUnits);
            Assert.Equal(1m, result.Data.Capacity.CurrentServingUnits);
            Assert.Equal(LicenseCapacityBandState.Normal, result.Data.Capacity.State);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/license")]
    [Endpoint("GET /api/v1/admin/license/status")]
    public async Task GetLicenseStatus_WithCustomExpiryWarningDays_UsesConfiguredThreshold()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        var licensePath = Path.Combine(tempDirectory.FullName, "license.honua-license.json");
        var license = LicenseTestSupport.CreateSignedLicense(
            HonuaEdition.Pro,
            expiresAt: DateTimeOffset.UtcNow.AddDays(45),
            entitlements: ["analytics.clustering"]);
        await File.WriteAllBytesAsync(licensePath, license.LicenseData);

        var fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Licensing:LicensePath"] = licensePath,
                        ["Licensing:ExpiryWarningDays"] = "60",
                        [$"Licensing:TrustedKeys:{LicenseTestSupport.KeyId}"] = license.PublicKeySetting
                    });
                });
            });

        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

            foreach (var path in new[] { "/api/v1/admin/license", "/api/v1/admin/license/status" })
            {
                var response = await client.GetAsync(path);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<ApiResponse<LicenseStatusResponse>>(json, _jsonOptions);

                Assert.NotNull(result);
                Assert.True(result.Success);
                Assert.NotNull(result.Data);
                Assert.True(result.Data.ExpiryWarning);
                Assert.InRange(result.Data.DaysUntilExpiry.GetValueOrDefault(), 44, 45);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
            tempDirectory.Delete(recursive: true);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/license")]
    public async Task GetLicenseStatus_WithoutAuth_ReturnsUnauthorized()
    {
        using var unauthClient = _fixture.CreateClient();
        var response = await unauthClient.GetAsync("/api/v1/admin/license");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/license")]
    public async Task UploadLicense_WhenAdminUploadDisabled_ReturnsBadRequest()
    {
        var licenseData = new StringContent("test-license-data", Encoding.UTF8, "application/octet-stream");
        var response = await _client.PostAsync("/api/v1/admin/license", licenseData);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<LicenseStatusResponse>>(json, _jsonOptions);

        Assert.NotNull(result);
        Assert.False(result.Success);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/license")]
    public async Task UploadLicense_EmptyData_ReturnsBadRequest()
    {
        var response = await _client.PostAsync("/api/v1/admin/license", new StringContent(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/license/entitlements")]
    public async Task GetEntitlements_WithAdminAuth_ReturnsEntitlementList()
    {
        var response = await _client.GetAsync("/api/v1/admin/license/entitlements");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<EntitlementResponse[]>>(json, _jsonOptions);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotEmpty(result.Data);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/license/capacity")]
    public async Task GetCapacity_WithAdminAuth_ReturnsMeterState()
    {
        var response = await _client.GetAsync("/api/v1/admin/license/capacity");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<LicenseCapacityState>>(json, _jsonOptions);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(LicenseCapacityBandState.NotConfigured, result.Data.State);
        Assert.Equal(1m, result.Data.CurrentServingUnits);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/license/capacity/surge")]
    public async Task SetSurgeMode_WithAdminAuth_ActivatesSurgeAccounting()
    {
        using var body = new StringContent(
            """{"enabled":true,"reason":"incident response"}""",
            Encoding.UTF8,
            "application/json");

        var response = await _client.PostAsync("/api/v1/admin/license/capacity/surge", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<LicenseCapacityState>>(json, _jsonOptions);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Surge.IsActive);
        Assert.Equal("incident response", result.Data.Surge.Reason);
    }
}

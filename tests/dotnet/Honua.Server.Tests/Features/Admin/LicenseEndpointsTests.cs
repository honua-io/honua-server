// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

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
        Assert.False(string.IsNullOrEmpty(result.Data.Edition));
        Assert.False(string.IsNullOrEmpty(result.Data.ValidationState));
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
    public async Task UploadLicense_ValidData_ReturnsUpdatedStatus()
    {
        var licenseData = new StringContent("test-license-data", Encoding.UTF8, "application/octet-stream");
        var response = await _client.PostAsync("/api/v1/admin/license", licenseData);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<LicenseStatusResponse>>(json, _jsonOptions);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.IsValid);
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
}

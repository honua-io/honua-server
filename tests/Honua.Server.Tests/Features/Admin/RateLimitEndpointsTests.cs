// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for rate limit policy admin endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.RateLimitManagement)]
public class RateLimitEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "ratelimit-admin-key";
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public RateLimitEndpointsTests()
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
    [Endpoint("GET /api/v1/admin/rate-limits")]
    public async Task ListPolicies_Empty_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/v1/admin/rate-limits");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<RateLimitPolicyResponse[]>>(json, _jsonOptions);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/rate-limits")]
    public async Task CreatePolicy_ValidRequest_ReturnsCreated()
    {
        var request = new CreateRateLimitPolicyRequest
        {
            Name = "API Key Limit",
            Scope = "api-key",
            Key = "test-key-1",
            RequestsPerWindow = 1000,
            WindowDurationSeconds = 60,
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/rate-limits", request, _jsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<RateLimitPolicyResponse>>(json, _jsonOptions);

        Assert.NotNull(result?.Data);
        Assert.Equal("API Key Limit", result.Data.Name);
        Assert.Equal(1000, result.Data.RequestsPerWindow);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/rate-limits/{id}")]
    public async Task GetPolicy_ExistingId_ReturnsPolicy()
    {
        // Create first
        var createRequest = new CreateRateLimitPolicyRequest
        {
            Name = "Get Test Policy",
            Scope = "tenant",
            Key = "tenant-1",
            RequestsPerWindow = 500,
            WindowDurationSeconds = 3600,
        };
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/rate-limits", createRequest, _jsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<RateLimitPolicyResponse>>(
            await createResponse.Content.ReadAsStringAsync(), _jsonOptions);

        // Get
        var response = await _client.GetAsync($"/api/v1/admin/rate-limits/{created!.Data!.PolicyId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<RateLimitPolicyResponse>>(json, _jsonOptions);
        Assert.Equal("Get Test Policy", result?.Data?.Name);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/rate-limits/{id}")]
    public async Task GetPolicy_NonExistentId_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/v1/admin/rate-limits/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/rate-limits/{id}")]
    public async Task UpdatePolicy_ValidRequest_ReturnsUpdated()
    {
        // Create first
        var createRequest = new CreateRateLimitPolicyRequest
        {
            Name = "Update Test",
            Scope = "endpoint",
            Key = "/api/v1/query",
            RequestsPerWindow = 100,
            WindowDurationSeconds = 60,
        };
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/rate-limits", createRequest, _jsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<RateLimitPolicyResponse>>(
            await createResponse.Content.ReadAsStringAsync(), _jsonOptions);

        // Update
        var updateRequest = new UpdateRateLimitPolicyRequest { RequestsPerWindow = 200 };
        var response = await _client.PutAsJsonAsync(
            $"/api/v1/admin/rate-limits/{created!.Data!.PolicyId}", updateRequest, _jsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<RateLimitPolicyResponse>>(json, _jsonOptions);
        Assert.Equal(200, result?.Data?.RequestsPerWindow);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/rate-limits/{id}")]
    public async Task DeletePolicy_ExistingId_ReturnsSuccess()
    {
        // Create first
        var createRequest = new CreateRateLimitPolicyRequest
        {
            Name = "Delete Test",
            Scope = "api-key",
            Key = "delete-key",
            RequestsPerWindow = 50,
            WindowDurationSeconds = 30,
        };
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/rate-limits", createRequest, _jsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<RateLimitPolicyResponse>>(
            await createResponse.Content.ReadAsStringAsync(), _jsonOptions);

        // Delete
        var response = await _client.DeleteAsync($"/api/v1/admin/rate-limits/{created!.Data!.PolicyId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/rate-limits/status")]
    public async Task GetStatus_MissingKey_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/v1/admin/rate-limits/status");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/rate-limits/status")]
    public async Task GetStatus_WithKey_ReturnsStatus()
    {
        // Create a policy first
        var createRequest = new CreateRateLimitPolicyRequest
        {
            Name = "Status Test",
            Scope = "api-key",
            Key = "status-key",
            RequestsPerWindow = 1000,
            WindowDurationSeconds = 60,
            Enabled = true,
        };
        await _client.PostAsJsonAsync("/api/v1/admin/rate-limits", createRequest, _jsonOptions);

        // Get status
        var response = await _client.GetAsync("/api/v1/admin/rate-limits/status?key=status-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<RateLimitStatusResponse>>(json, _jsonOptions);

        Assert.NotNull(result?.Data);
        Assert.Equal("status-key", result.Data.Key);
    }
}

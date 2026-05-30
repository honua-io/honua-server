// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Services;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for user management admin endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public class UserManagementEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "user-mgmt-admin-key";
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public UserManagementEndpointsTests()
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

        // Seed test users
        var store = _fixture.Services.GetRequiredService<IUserStore>() as InMemoryUserStore;
        store?.Seed(new ManagedUser
        {
            UserId = "user-1",
            DisplayName = "Test User One",
            Email = "user1@example.com",
            ProvisioningSource = "oidc",
            Roles = ["viewer"],
        });
        store?.Seed(new ManagedUser
        {
            UserId = "user-2",
            DisplayName = "Test User Two",
            Email = "user2@example.com",
            ProvisioningSource = "scim",
            Roles = ["editor"],
        });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/users")]
    public async Task ListUsers_WithAdminAuth_ReturnsUsers()
    {
        var response = await _client.GetAsync("/api/v1/admin/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<UserListResponse>>(json, _jsonOptions);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.TotalCount >= 2);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/users")]
    public async Task ListUsers_FilterBySource_ReturnsFiltered()
    {
        var response = await _client.GetAsync("/api/v1/admin/users?source=oidc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<UserListResponse>>(json, _jsonOptions);

        Assert.NotNull(result?.Data);
        Assert.All(result.Data.Users, u => Assert.Equal("oidc", u.ProvisioningSource));
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/users/{id}")]
    public async Task GetUser_ExistingId_ReturnsUserDetails()
    {
        var response = await _client.GetAsync("/api/v1/admin/users/user-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<UserResponse>>(json, _jsonOptions);

        Assert.NotNull(result?.Data);
        Assert.Equal("user-1", result.Data.UserId);
        Assert.Equal("Test User One", result.Data.DisplayName);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/users/{id}")]
    public async Task GetUser_NonExistentId_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/users/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/users/{id}/roles")]
    public async Task UpdateUserRoles_ValidRequest_ReturnsUpdatedUser()
    {
        var request = new UpdateUserRolesRequest { Roles = ["admin", "editor"] };
        var response = await _client.PutAsJsonAsync("/api/v1/admin/users/user-1/roles", request, _jsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<UserResponse>>(json, _jsonOptions);

        Assert.NotNull(result?.Data);
        Assert.Contains("admin", result.Data.Roles);
        Assert.Contains("editor", result.Data.Roles);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/users/{id}/roles")]
    public async Task UpdateUserRoles_NonExistentUser_ReturnsNotFound()
    {
        var request = new UpdateUserRolesRequest { Roles = ["viewer"] };
        var response = await _client.PutAsJsonAsync("/api/v1/admin/users/nonexistent/roles", request, _jsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/users/{id}")]
    public async Task DeleteUser_ExistingId_ReturnsSuccess()
    {
        var response = await _client.DeleteAsync("/api/v1/admin/users/user-2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify deprovisioned (still exists but inactive)
        var getResponse = await _client.GetAsync("/api/v1/admin/users/user-2");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var json = await getResponse.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<UserResponse>>(json, _jsonOptions);
        Assert.False(result?.Data?.IsActive);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/users/{id}/effective-permissions")]
    public async Task GetEffectivePermissions_ExistingUser_ReturnsPermissions()
    {
        var response = await _client.GetAsync("/api/v1/admin/users/user-1/effective-permissions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<EffectivePermissionsResponse>>(json, _jsonOptions);

        Assert.NotNull(result?.Data);
        Assert.Equal("user-1", result.Data.UserId);
        Assert.NotNull(result.Data.Permissions);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/users/{id}/effective-permissions")]
    public async Task GetEffectivePermissions_NonExistentUser_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/users/nonexistent/effective-permissions");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

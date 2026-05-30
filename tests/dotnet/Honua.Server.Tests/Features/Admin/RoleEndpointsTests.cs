// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for role and permission management admin endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.RoleManagement)]
public class RoleEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "role-admin-key";
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public RoleEndpointsTests()
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
    [Endpoint("GET /api/v1/admin/roles")]
    public async Task ListRoles_WithAdminAuth_ReturnsBuiltInRoles()
    {
        var response = await _client.GetAsync("/api/v1/admin/roles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<RoleResponse[]>>(json, _jsonOptions);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Length >= 2); // admin + viewer built-in
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/roles")]
    public async Task CreateRole_ValidRequest_ReturnsCreated()
    {
        var request = new CreateRoleRequest
        {
            Name = "field-worker",
            Description = "Field data collection role",
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/roles", request, _jsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<RoleResponse>>(json, _jsonOptions);

        Assert.NotNull(result?.Data);
        Assert.Equal("field-worker", result.Data.Name);
        Assert.False(result.Data.IsBuiltIn);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/roles/{id}")]
    public async Task GetRole_BuiltInRole_ReturnsRole()
    {
        // Known built-in admin role ID
        var adminRoleId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var response = await _client.GetAsync($"/api/v1/admin/roles/{adminRoleId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<RoleResponse>>(json, _jsonOptions);

        Assert.NotNull(result?.Data);
        Assert.Equal("admin", result.Data.Name);
        Assert.True(result.Data.IsBuiltIn);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/roles/{id}")]
    public async Task GetRole_NonExistentId_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/v1/admin/roles/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/roles/{id}")]
    public async Task UpdateRole_ValidRequest_ReturnsUpdated()
    {
        // Create a role first
        var createRequest = new CreateRoleRequest { Name = "update-test" };
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/roles", createRequest, _jsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<RoleResponse>>(
            await createResponse.Content.ReadAsStringAsync(), _jsonOptions);

        // Update
        var updateRequest = new UpdateRoleRequest { Description = "Updated description" };
        var response = await _client.PutAsJsonAsync(
            $"/api/v1/admin/roles/{created!.Data!.RoleId}", updateRequest, _jsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<RoleResponse>>(json, _jsonOptions);
        Assert.Equal("Updated description", result?.Data?.Description);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/roles/{id}")]
    public async Task DeleteRole_CustomRole_ReturnsSuccess()
    {
        // Create a role first
        var createRequest = new CreateRoleRequest { Name = "delete-test" };
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/roles", createRequest, _jsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<RoleResponse>>(
            await createResponse.Content.ReadAsStringAsync(), _jsonOptions);

        // Delete
        var response = await _client.DeleteAsync($"/api/v1/admin/roles/{created!.Data!.RoleId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/roles/{id}")]
    public async Task DeleteRole_BuiltInRole_ReturnsNotFound()
    {
        var adminRoleId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var response = await _client.DeleteAsync($"/api/v1/admin/roles/{adminRoleId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/roles/{id}/permissions")]
    public async Task GetPermissions_BuiltInRole_ReturnsPermissions()
    {
        var adminRoleId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var response = await _client.GetAsync($"/api/v1/admin/roles/{adminRoleId}/permissions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<PermissionGrantResponse[]>>(json, _jsonOptions);

        Assert.NotNull(result?.Data);
        Assert.NotEmpty(result.Data);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/roles/{id}/permissions")]
    public async Task SetPermissions_ValidRequest_ReturnsUpdatedPermissions()
    {
        // Create a role first
        var createRequest = new CreateRoleRequest { Name = "perm-test" };
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/roles", createRequest, _jsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<RoleResponse>>(
            await createResponse.Content.ReadAsStringAsync(), _jsonOptions);

        // Set permissions
        var permRequest = new SetPermissionsRequest
        {
            Permissions =
            [
                new PermissionGrantRequest { Service = "my-service", Layer = "*", Operation = "read" },
                new PermissionGrantRequest { Service = "my-service", Layer = "layer-1", Operation = "write" },
            ],
        };

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/admin/roles/{created!.Data!.RoleId}/permissions", permRequest, _jsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<PermissionGrantResponse[]>>(json, _jsonOptions);

        Assert.NotNull(result?.Data);
        Assert.Equal(2, result.Data.Length);
    }
}

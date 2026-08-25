// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Console.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Console;

/// <summary>Integration coverage for the Console Access RBAC contract.</summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.RoleManagement)]
public sealed class ConsoleAccessEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "console-access-admin-key";
    private const string WorkspaceId = "default";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .UseSeed("tests/seed/server.yaml")
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        });

    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(client =>
            client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/access/{workspaceId}/roles")]
    [Endpoint("GET /api/v1/console/access/{workspaceId}/members")]
    [Endpoint("POST /api/v1/console/access/{workspaceId}/roles")]
    [Endpoint("PUT /api/v1/console/access/{workspaceId}/roles/{roleId}")]
    [Endpoint("DELETE /api/v1/console/access/{workspaceId}/roles/{roleId}")]
    [Endpoint("GET /api/v1/console/access/{workspaceId}/roles/audit")]
    public async Task ConsoleAccess_AdminLifecycle_ProjectsRolesMembersAndAudit()
    {
        var overviewResponse = await _client.GetAsync($"/api/v1/console/access/{WorkspaceId}/roles");
        Assert.Equal(HttpStatusCode.OK, overviewResponse.StatusCode);
        var overview = await ReadAsync<ConsoleRbacOverview>(overviewResponse);
        Assert.Equal(WorkspaceId, overview.WorkspaceId);
        Assert.Contains(overview.Roles, static role => role.Name == "admin" && !role.IsCustom);
        Assert.Contains(overview.Permissions, static permission => permission.Key == "manage-roles");
        Assert.True(overview.CanManageRoles);

        var membersResponse = await _client.GetAsync($"/api/v1/console/access/{WorkspaceId}/members");
        Assert.Equal(HttpStatusCode.OK, membersResponse.StatusCode);
        var membership = await ReadAsync<ConsoleTeamMembership>(membersResponse);
        Assert.Equal(WorkspaceId, membership.WorkspaceId);
        Assert.True(membership.CanInvite);

        var createRequest = new ConsoleRoleWriteRequest
        {
            Name = "dashboard-publisher",
            Description = "Publishes approved dashboards.",
            Grants =
            [
                new ConsolePermissionGrant { Permission = "view-public", Grant = "granted" },
                new ConsolePermissionGrant { Permission = "publish", Grant = "granted" },
            ],
        };
        var createResponse = await _client.PostAsJsonAsync(
            $"/api/v1/console/access/{WorkspaceId}/roles", createRequest, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadAsync<ConsoleRbacRole>(createResponse);
        Assert.True(created.IsCustom);
        Assert.Contains(created.Grants, static grant => grant.Permission == "publish" && grant.Grant == "granted");

        var updateRequest = createRequest with
        {
            Name = "dashboard-reviewer",
            Grants =
            [
                new ConsolePermissionGrant { Permission = "view-public", Grant = "granted" },
                new ConsolePermissionGrant { Permission = "comment", Grant = "granted" },
            ],
        };
        var updateResponse = await _client.PutAsJsonAsync(
            $"/api/v1/console/access/{WorkspaceId}/roles/{created.Id}", updateRequest, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await ReadAsync<ConsoleRbacRole>(updateResponse);
        Assert.Equal("dashboard-reviewer", updated.Name);
        Assert.Contains(updated.Grants, static grant => grant.Permission == "comment" && grant.Grant == "granted");
        Assert.Contains(updated.Grants, static grant => grant.Permission == "publish" && grant.Grant == "not-granted");

        var auditResponse = await _client.GetAsync(
            $"/api/v1/console/access/{WorkspaceId}/roles/audit?pageSize=50");
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        var audit = await ReadAsync<ConsoleRoleAuditPage>(auditResponse);
        Assert.Contains(audit.Entries, entry =>
            entry.RoleId == created.Id && entry.Action == "console_access.role.create");
        Assert.Contains(audit.Entries, entry =>
            entry.RoleId == created.Id && entry.Action == "console_access.role.update");

        var deleteResponse = await _client.DeleteAsync(
            $"/api/v1/console/access/{WorkspaceId}/roles/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        var afterDelete = await _client.GetAsync($"/api/v1/console/access/{WorkspaceId}/roles");
        Assert.DoesNotContain((await ReadAsync<ConsoleRbacOverview>(afterDelete)).Roles, role => role.Id == created.Id);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/access/{workspaceId}/roles")]
    public async Task ConsoleAccess_WithoutAdminAuthentication_ReturnsUnauthorized()
    {
        using var anonymous = _fixture.CreateClient();
        var response = await anonymous.GetAsync($"/api/v1/console/access/{WorkspaceId}/roles");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var envelope = JsonSerializer.Deserialize<ApiResponse<T>>(
            await response.Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope.Success, envelope.Message);
        return Assert.IsType<T>(envelope.Data);
    }
}

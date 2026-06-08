// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Server.Features.Console.Models;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Console;

/// <summary>
/// Integration tests for the Console Access (RBAC) workspace-scoped read API
/// (honua-server#1162). The server projects the global admin RBAC roles into the
/// workspace-scoped console Access shapes: the roles overview returns the scope
/// hierarchy + permission columns + projected role rows; the members read returns
/// an honest empty-but-valid roster (no server-side membership store in this build).
/// Both reads return empty-but-valid 200s for any workspace, never 404, and require
/// admin authorization.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.RoleManagement)]
public sealed class ConsoleAccessEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "console-access-admin-key";
    private const string WorkspaceId = "default";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebAppFixture _fixture;
    private HttpClient _adminClient = null!;
    private HttpClient _anonymousClient = null!;

    public ConsoleAccessEndpointsTests()
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
        _adminClient = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        _anonymousClient = _fixture.CreateClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/access/{workspaceId}/roles")]
    public async Task GetRoles_ReturnsScopeHierarchyPermissionColumnsAndProjectedRoles()
    {
        var response = await _adminClient.GetAsync($"/api/v1/console/access/{WorkspaceId}/roles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var overview = await ReadDataAsync<ConsoleRbacOverview>(response);

        Assert.Equal(WorkspaceId, overview.WorkspaceId);

        // Static, server-owned scope hierarchy strip (workspace > ... > resource-field).
        Assert.Equal(5, overview.Scopes.Count);
        Assert.Equal("workspace", overview.Scopes[0].Level);

        // Fixed console permission columns.
        Assert.Contains(overview.Permissions, p => p.Key == "manage-content");
        Assert.Contains(overview.Permissions, p => p.Key == "view");

        // The overview is built from whatever roles the global admin RBAC store holds (possibly none on a
        // fresh store) — the read never 404s and aggregate counts are consistent with the projected rows.
        Assert.NotNull(overview.Roles);
        Assert.Equal(overview.Roles.Count, overview.BuiltInRoleCount + overview.CustomRoleCount);

        // Every projected role row carries one grant per permission column, each grant referencing a known
        // column with a non-empty grant kind (the global service/layer/operation grants folded into columns).
        foreach (var role in overview.Roles)
        {
            Assert.Equal(overview.Permissions.Count, role.Grants.Count);
            foreach (var grant in role.Grants)
            {
                Assert.Contains(overview.Permissions, p => p.Key == grant.Permission);
                Assert.False(string.IsNullOrWhiteSpace(grant.Grant));
            }
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/access/{workspaceId}/roles")]
    public async Task GetRoles_ForUnconfiguredWorkspace_ReturnsOverviewNot404()
    {
        const string unconfiguredWorkspace = "never-seeded";
        var response = await _adminClient.GetAsync($"/api/v1/console/access/{unconfiguredWorkspace}/roles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var overview = await ReadDataAsync<ConsoleRbacOverview>(response);
        Assert.Equal(unconfiguredWorkspace, overview.WorkspaceId);
        // Global roles are surfaced for every workspace; the read never 404s.
        Assert.NotNull(overview.Roles);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/access/{workspaceId}/members")]
    public async Task GetMembers_ReturnsHonestEmptyRosterWith200()
    {
        var response = await _adminClient.GetAsync($"/api/v1/console/access/{WorkspaceId}/members");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await ReadDataAsync<ConsoleTeamMembership>(response);

        Assert.Equal(WorkspaceId, membership.WorkspaceId);
        Assert.Empty(membership.Members);
        Assert.Empty(membership.Invitations);
        Assert.Equal(0, membership.ActiveCount);
        Assert.Equal(0, membership.PendingCount);
        Assert.Equal(0, membership.DeactivatedCount);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/access/{workspaceId}/roles")]
    [Endpoint("GET /api/v1/console/access/{workspaceId}/members")]
    public async Task AnonymousRequests_AreRejectedWithoutAdminAuthorization()
    {
        var rolesResponse = await _anonymousClient.GetAsync($"/api/v1/console/access/{WorkspaceId}/roles");
        Assert.Equal(HttpStatusCode.Unauthorized, rolesResponse.StatusCode);

        var membersResponse = await _anonymousClient.GetAsync($"/api/v1/console/access/{WorkspaceId}/members");
        Assert.Equal(HttpStatusCode.Unauthorized, membersResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/access/{workspaceId}/roles")]
    [Endpoint("PUT /api/v1/console/access/{workspaceId}/roles/{roleId}")]
    [Endpoint("GET /api/v1/console/access/{workspaceId}/roles/audit")]
    [Endpoint("DELETE /api/v1/console/access/{workspaceId}/roles/{roleId}")]
    public async Task CustomRole_CreateUpdateAuditDelete_Lifecycle()
    {
        // Create a custom role from the console permission columns.
        var createRequest = new ConsoleRoleWriteRequest
        {
            Name = "console-access-lifecycle-role",
            Description = "Created by ConsoleAccessEndpointsTests",
            Grants =
            [
                new ConsoleRbacGrant { Permission = "view", Grant = "granted" },
                new ConsoleRbacGrant { Permission = "manage-content", Grant = "granted" },
                new ConsoleRbacGrant { Permission = "manage-roles", Grant = "not-granted" },
            ],
        };

        var createResponse = await _adminClient.PostAsJsonAsync(
            $"/api/v1/console/access/{WorkspaceId}/roles", createRequest, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadDataAsync<ConsoleRbacRole>(createResponse);
        Assert.True(created.IsCustom);
        Assert.Equal(createRequest.Name, created.Name);
        // The granted columns fold back into grants; "view" is granted, "manage-roles" is not.
        Assert.Contains(created.Grants, g => g.Permission == "view" && g.Grant != "not-granted");
        Assert.Contains(created.Grants, g => g.Permission == "manage-roles" && g.Grant == "not-granted");

        // Update the role's description and grants.
        var updateRequest = new ConsoleRoleWriteRequest
        {
            Name = created.Name,
            Description = "Updated by ConsoleAccessEndpointsTests",
            Grants = [new ConsoleRbacGrant { Permission = "view", Grant = "granted" }],
        };
        var updateResponse = await _adminClient.PutAsJsonAsync(
            $"/api/v1/console/access/{WorkspaceId}/roles/{created.Id}", updateRequest, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await ReadDataAsync<ConsoleRbacRole>(updateResponse);
        Assert.Equal(created.Id, updated.Id);

        // The role-change audit trail is queryable and returns an empty-or-populated page (audit writes are
        // best-effort/asynchronous, so we assert the route works and the page shape is valid, not timing).
        var auditResponse = await _adminClient.GetAsync(
            $"/api/v1/console/access/{WorkspaceId}/roles/audit?pageSize=20");
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        var auditPage = await ReadDataAsync<ConsoleRoleAuditPage>(auditResponse);
        Assert.NotNull(auditPage.Entries);

        // Delete the custom role.
        var deleteResponse = await _adminClient.DeleteAsync(
            $"/api/v1/console/access/{WorkspaceId}/roles/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/access/{workspaceId}/roles")]
    public async Task CreateRole_WithBlankName_ReturnsBadRequest()
    {
        var response = await _adminClient.PostAsJsonAsync(
            $"/api/v1/console/access/{WorkspaceId}/roles",
            new ConsoleRoleWriteRequest { Name = "   " },
            JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<ApiResponse<T>>(body, JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success, body);
        Assert.NotNull(envelope.Data);
        return envelope.Data!;
    }
}

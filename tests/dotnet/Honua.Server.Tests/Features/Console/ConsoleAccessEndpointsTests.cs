// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Console.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

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
        Assert.False(membership.CanInvite);

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
            Name = created.Name,
            Description = "Reviews and comments on dashboards.",
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
        Assert.Equal(created.Name, updated.Name);
        Assert.Equal("Reviews and comments on dashboards.", updated.Description);
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

    [IntegrationTest]
    [Endpoint("PUT /api/v1/console/access/{workspaceId}/roles/{roleId}")]
    [Endpoint("DELETE /api/v1/console/access/{workspaceId}/roles/{roleId}")]
    public async Task ConsoleAccess_RoleMutations_CannotCrossWorkspaceBoundary()
    {
        const string ownerWorkspace = "mutation-owner";
        const string otherWorkspace = "mutation-other";
        var created = await CreateRoleAsync(ownerWorkspace, "owner-only-role");
        var update = new ConsoleRoleWriteRequest
        {
            Name = "cross-workspace-update",
            Grants = [],
        };

        var updateResponse = await _client.PutAsJsonAsync(
            $"/api/v1/console/access/{otherWorkspace}/roles/{created.Id}", update, JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, updateResponse.StatusCode);

        var deleteResponse = await _client.DeleteAsync(
            $"/api/v1/console/access/{otherWorkspace}/roles/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, deleteResponse.StatusCode);

        var ownerOverview = await ReadAsync<ConsoleRbacOverview>(
            await _client.GetAsync($"/api/v1/console/access/{ownerWorkspace}/roles"));
        Assert.Contains(ownerOverview.Roles, role => role.Id == created.Id && role.Name == created.Name);

        var otherOverview = await ReadAsync<ConsoleRbacOverview>(
            await _client.GetAsync($"/api/v1/console/access/{otherWorkspace}/roles"));
        Assert.DoesNotContain(otherOverview.Roles, role => role.Id == created.Id);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/access/{workspaceId}/roles")]
    [Endpoint("PUT /api/v1/console/access/{workspaceId}/roles/{roleId}")]
    public async Task ConsoleAccess_RoleNames_AreGloballyUniqueOnCreate()
    {
        const string firstWorkspace = "name-owner";
        const string secondWorkspace = "name-other";
        const string sharedName = "shared-workspace-role";
        var firstRole = await CreateRoleAsync(firstWorkspace, sharedName);

        var createCollision = await _client.PostAsJsonAsync(
            $"/api/v1/console/access/{secondWorkspace}/roles",
            new ConsoleRoleWriteRequest { Name = sharedName.ToUpperInvariant(), Grants = [] },
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, createCollision.StatusCode);
        await AssertRoleNameConflictAsync(createCollision);

        // Permission and description updates preserve the immutable role name.
        var preserveOwnName = await _client.PutAsJsonAsync(
            $"/api/v1/console/access/{firstWorkspace}/roles/{firstRole.Id}",
            new ConsoleRoleWriteRequest { Name = firstRole.Name, Grants = [] },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, preserveOwnName.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/console/access/{workspaceId}/roles/{roleId}")]
    public async Task ConsoleAccess_RoleRename_WithExistingAssignment_ReturnsBadRequestWithoutOrphaningMember()
    {
        const string workspace = "immutable-role-name";
        var role = await CreateRoleAsync(workspace, "assigned-reviewer");
        var userStore = _fixture.Services.GetRequiredService<IScimUserStore>();
        var assignedUser = Assert.IsType<ManagedUser>(await userStore.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "assigned-reviewer@example.test",
            DisplayName = "Assigned Reviewer",
            Roles = [role.Name],
        }));

        var renameResponse = await _client.PutAsJsonAsync(
            $"/api/v1/console/access/{workspace}/roles/{role.Id}",
            new ConsoleRoleWriteRequest { Name = "renamed-reviewer", Grants = [] },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, renameResponse.StatusCode);
        var error = JsonSerializer.Deserialize<ApiResponse<object>>(
            await renameResponse.Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(error);
        Assert.False(error.Success);
        Assert.Contains("cannot be changed", Assert.IsType<string>(error.Message), StringComparison.OrdinalIgnoreCase);

        var overview = await ReadAsync<ConsoleRbacOverview>(
            await _client.GetAsync($"/api/v1/console/access/{workspace}/roles"));
        Assert.Contains(overview.Roles, candidate => candidate.Id == role.Id && candidate.Name == role.Name);
        Assert.DoesNotContain(overview.Roles, static candidate => candidate.Name == "renamed-reviewer");

        var membership = await ReadAsync<ConsoleTeamMembership>(
            await _client.GetAsync($"/api/v1/console/access/{workspace}/members"));
        Assert.Contains(membership.Members, member =>
            member.Id == assignedUser.UserId && member.RoleId == role.Id && member.RoleName == role.Name);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/roles/{id}")]
    public async Task AdminRoleUpdate_RenameAssignedConsoleRole_ReturnsBadRequestWithoutOrphaningMember()
    {
        const string workspace = "admin-immutable-role-name";
        var role = await CreateRoleAsync(workspace, "admin-assigned-reviewer");
        var userStore = _fixture.Services.GetRequiredService<IScimUserStore>();
        var assignedUser = Assert.IsType<ManagedUser>(await userStore.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "admin-assigned-reviewer@example.test",
            DisplayName = "Admin Assigned Reviewer",
            Roles = [role.Name],
        }));

        var renameResponse = await _client.PutAsJsonAsync(
            $"/api/v1/admin/roles/{role.Id}",
            new UpdateRoleRequest { Name = "admin-renamed-reviewer" },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, renameResponse.StatusCode);
        var error = JsonSerializer.Deserialize<ApiResponse<object>>(
            await renameResponse.Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(error);
        Assert.False(error.Success);
        Assert.Contains("cannot be changed", Assert.IsType<string>(error.Message), StringComparison.OrdinalIgnoreCase);

        var membership = await ReadAsync<ConsoleTeamMembership>(
            await _client.GetAsync($"/api/v1/console/access/{workspace}/members"));
        Assert.Contains(membership.Members, member =>
            member.Id == assignedUser.UserId && member.RoleId == role.Id && member.RoleName == role.Name);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/access/{workspaceId}/members")]
    public async Task ConsoleAccess_Members_OnlyIncludeRolesAssignedInRequestedWorkspace()
    {
        const string firstWorkspace = "members-first";
        const string secondWorkspace = "members-second";
        var firstRole = await CreateRoleAsync(firstWorkspace, "first-workspace-member");
        var secondRole = await CreateRoleAsync(secondWorkspace, "second-workspace-member");
        var userStore = _fixture.Services.GetRequiredService<IScimUserStore>();
        var firstOnly = Assert.IsType<ManagedUser>(await userStore.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "console-first@example.test",
            DisplayName = "Console First",
            Roles = [firstRole.Name],
        }));
        var secondOnly = Assert.IsType<ManagedUser>(await userStore.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "console-second@example.test",
            DisplayName = "Console Second",
            Roles = [secondRole.Name],
        }));
        var both = Assert.IsType<ManagedUser>(await userStore.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "console-both@example.test",
            DisplayName = "Console Both",
            Roles = [firstRole.Name, secondRole.Name],
        }));
        var builtInOnly = Assert.IsType<ManagedUser>(await userStore.CreateUserAsync(new ScimUserProvisioning
        {
            UserName = "console-global-admin@example.test",
            DisplayName = "Console Global Admin",
            Roles = ["admin"],
        }));

        var firstMembership = await ReadAsync<ConsoleTeamMembership>(
            await _client.GetAsync($"/api/v1/console/access/{firstWorkspace}/members"));
        Assert.Contains(firstMembership.Members, member => member.Id == firstOnly.UserId);
        Assert.Contains(firstMembership.Members, member =>
            member.Id == both.UserId && member.RoleName == firstRole.Name);
        Assert.DoesNotContain(firstMembership.Members, member => member.Id == secondOnly.UserId);
        Assert.DoesNotContain(firstMembership.Members, member => member.Id == builtInOnly.UserId);

        var secondMembership = await ReadAsync<ConsoleTeamMembership>(
            await _client.GetAsync($"/api/v1/console/access/{secondWorkspace}/members"));
        Assert.Contains(secondMembership.Members, member => member.Id == secondOnly.UserId);
        Assert.Contains(secondMembership.Members, member =>
            member.Id == both.UserId && member.RoleName == secondRole.Name);
        Assert.DoesNotContain(secondMembership.Members, member => member.Id == firstOnly.UserId);
        Assert.DoesNotContain(secondMembership.Members, member => member.Id == builtInOnly.UserId);

        var firstOverview = await ReadAsync<ConsoleRbacOverview>(
            await _client.GetAsync($"/api/v1/console/access/{firstWorkspace}/roles"));
        var secondOverview = await ReadAsync<ConsoleRbacOverview>(
            await _client.GetAsync($"/api/v1/console/access/{secondWorkspace}/roles"));
        Assert.Equal(2, firstOverview.MembersAffected);
        Assert.Equal(2, secondOverview.MembersAffected);
    }

    private async Task<ConsoleRbacRole> CreateRoleAsync(string workspaceId, string name)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/console/access/{workspaceId}/roles",
            new ConsoleRoleWriteRequest
            {
                Name = name,
                Grants = [],
            },
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<ConsoleRbacRole>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var envelope = JsonSerializer.Deserialize<ApiResponse<T>>(
            await response.Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope.Success, envelope.Message);
        return Assert.IsType<T>(envelope.Data);
    }

    private static async Task AssertRoleNameConflictAsync(HttpResponseMessage response)
    {
        var envelope = JsonSerializer.Deserialize<ApiResponse<object>>(
            await response.Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope.Success);
        var message = Assert.IsType<string>(envelope.Message);
        Assert.Contains("unique across workspaces", message, StringComparison.OrdinalIgnoreCase);
    }
}

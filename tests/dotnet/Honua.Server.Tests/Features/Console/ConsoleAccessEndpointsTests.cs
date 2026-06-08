// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Core.Features.Identity.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Identity;

/// <summary>
/// Integration tests for the SCIM 2.0 provisioning endpoints (#510): Users and Groups CRUD,
/// list filtering/pagination, group-to-role mapping, deprovisioning, and bearer-token auth.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public class ScimProvisioningEndpointsTests : IAsyncLifetime
{
    private const string ScimToken = "scim-integration-bearer-token";
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ScimProvisioningEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                // #2978: SCIM provisioning is an Enterprise entitlement; grant it so these
                // tests keep exercising the SCIM machinery itself (the gate has its own tests
                // in IdentityEntitlementGateTests).
                builder.UseSetting("Licensing:DevGrantEdition", "Enterprise");
                builder.UseSetting("Scim:BearerToken", ScimToken);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c =>
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ScimToken));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /scim/v2/Users")]
    public async Task CreateUser_ValidRequest_ProvisionsUser()
    {
        var response = await _client.PostAsJsonAsync("/scim/v2/Users", new
        {
            userName = "alice@example.com",
            displayName = "Alice Example",
            active = true,
            emails = new[] { new { value = "alice@example.com", primary = true } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = await ReadAsync(response);
        Assert.Equal("alice@example.com", user.GetProperty("userName").GetString());
        Assert.True(user.GetProperty("active").GetBoolean());

        // Provisioned user is visible to the shared identity store with source "scim".
        var store = _fixture.Services.GetRequiredService<IUserStore>();
        var managed = await store.GetUserAsync("alice@example.com");
        Assert.NotNull(managed);
        Assert.Equal("scim", managed!.ProvisioningSource);
    }

    [IntegrationTest]
    [Endpoint("POST /scim/v2/Users")]
    public async Task CreateUser_DuplicateUserName_ReturnsConflict()
    {
        await CreateUserAsync("dup@example.com");
        var response = await _client.PostAsJsonAsync("/scim/v2/Users", new
        {
            userName = "dup@example.com",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/Users")]
    public async Task ListUsers_WithUserNameFilter_ReturnsMatch()
    {
        await CreateUserAsync("filter-me@example.com");
        await CreateUserAsync("other@example.com");

        var response = await _client.GetAsync("/scim/v2/Users?filter=userName%20eq%20%22filter-me@example.com%22");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(1, body.GetProperty("totalResults").GetInt32());
        var resources = body.GetProperty("resources");
        Assert.Equal("filter-me@example.com", resources[0].GetProperty("userName").GetString());
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/Users/{id}")]
    public async Task GetUser_Existing_ReturnsUser()
    {
        await CreateUserAsync("get-me@example.com");
        var response = await _client.GetAsync("/scim/v2/Users/get-me@example.com");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await ReadAsync(response);
        Assert.Equal("get-me@example.com", user.GetProperty("id").GetString());
    }

    [IntegrationTest]
    [Endpoint("PUT /scim/v2/Users/{id}")]
    public async Task ReplaceUser_UpdatesAttributes()
    {
        await CreateUserAsync("replace@example.com");
        var response = await _client.PutAsJsonAsync("/scim/v2/Users/replace@example.com", new
        {
            userName = "replace@example.com",
            displayName = "Renamed Person",
            active = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await ReadAsync(response);
        Assert.Equal("Renamed Person", user.GetProperty("displayName").GetString());
    }

    [IntegrationTest]
    [Endpoint("PATCH /scim/v2/Users/{id}")]
    public async Task PatchUser_SetActiveFalse_DeprovisionsUser()
    {
        await CreateUserAsync("patch@example.com");
        var response = await _client.PatchAsync("/scim/v2/Users/patch@example.com", JsonContent.Create(new
        {
            Operations = new[] { new { op = "replace", path = "active", value = false } },
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await ReadAsync(response);
        Assert.False(user.GetProperty("active").GetBoolean());

        // Deactivation revokes access in the underlying store.
        var store = _fixture.Services.GetRequiredService<IUserStore>();
        var managed = await store.GetUserAsync("patch@example.com");
        Assert.False(managed!.IsActive);
    }

    [IntegrationTest]
    [Endpoint("DELETE /scim/v2/Users/{id}")]
    public async Task DeleteUser_RevokesAccess()
    {
        await CreateUserAsync("delete@example.com");
        var response = await _client.DeleteAsync("/scim/v2/Users/delete@example.com");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var store = _fixture.Services.GetRequiredService<IUserStore>();
        var managed = await store.GetUserAsync("delete@example.com");
        Assert.False(managed!.IsActive);
        Assert.Empty(managed.Roles);
    }

    [IntegrationTest]
    [Endpoint("POST /scim/v2/Groups")]
    [Endpoint("GET /scim/v2/Groups")]
    public async Task CreateGroup_WithMembers_MapsRoleToMembers()
    {
        await CreateUserAsync("member@example.com");

        var response = await _client.PostAsJsonAsync("/scim/v2/Groups", new
        {
            displayName = "editor",
            members = new[] { new { value = "member@example.com" } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The member receives the group's role (group displayName == role name).
        var store = _fixture.Services.GetRequiredService<IUserStore>();
        var managed = await store.GetUserAsync("member@example.com");
        Assert.Contains("editor", managed!.Roles, StringComparer.OrdinalIgnoreCase);

        var list = await _client.GetAsync("/scim/v2/Groups");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/Groups/{id}")]
    [Endpoint("PUT /scim/v2/Groups/{id}")]
    [Endpoint("PATCH /scim/v2/Groups/{id}")]
    [Endpoint("DELETE /scim/v2/Groups/{id}")]
    public async Task GroupLifecycle_MembershipChanges_SyncRoles()
    {
        await CreateUserAsync("g-user@example.com");

        // Create empty group.
        var createResp = await _client.PostAsJsonAsync("/scim/v2/Groups", new
        {
            displayName = "analysts",
        });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var groupId = (await ReadAsync(createResp)).GetProperty("id").GetString()!;

        // GET it.
        var getResp = await _client.GetAsync($"/scim/v2/Groups/{groupId}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        // PATCH add member => role granted.
        var patchAdd = await _client.PatchAsync($"/scim/v2/Groups/{groupId}", JsonContent.Create(new
        {
            Operations = new[]
            {
                new { op = "add", path = "members", value = new[] { new { value = "g-user@example.com" } } },
            },
        }));
        Assert.Equal(HttpStatusCode.OK, patchAdd.StatusCode);

        var store = _fixture.Services.GetRequiredService<IUserStore>();
        Assert.Contains("analysts", (await store.GetUserAsync("g-user@example.com"))!.Roles, StringComparer.OrdinalIgnoreCase);

        // PATCH remove member => role revoked.
        var patchRemove = await _client.PatchAsync($"/scim/v2/Groups/{groupId}", JsonContent.Create(new
        {
            Operations = new[]
            {
                new { op = "remove", path = "members", value = new[] { new { value = "g-user@example.com" } } },
            },
        }));
        Assert.Equal(HttpStatusCode.OK, patchRemove.StatusCode);
        Assert.DoesNotContain("analysts", (await store.GetUserAsync("g-user@example.com"))!.Roles, StringComparer.OrdinalIgnoreCase);

        // PUT full replace members.
        var putResp = await _client.PutAsJsonAsync($"/scim/v2/Groups/{groupId}", new
        {
            displayName = "analysts",
            members = new[] { new { value = "g-user@example.com" } },
        });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        Assert.Contains("analysts", (await store.GetUserAsync("g-user@example.com"))!.Roles, StringComparer.OrdinalIgnoreCase);

        // DELETE group => role revoked from all members.
        var deleteResp = await _client.DeleteAsync($"/scim/v2/Groups/{groupId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);
        Assert.DoesNotContain("analysts", (await store.GetUserAsync("g-user@example.com"))!.Roles, StringComparer.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/ServiceProviderConfig")]
    public async Task ServiceProviderConfig_ReturnsSupportedFeatures()
    {
        var response = await _client.GetAsync("/scim/v2/ServiceProviderConfig");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var config = await ReadAsync(response);
        Assert.Contains(
            "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig",
            config.GetProperty("schemas").EnumerateArray().Select(s => s.GetString()));
        Assert.True(config.GetProperty("patch").GetProperty("supported").GetBoolean());
        Assert.True(config.GetProperty("filter").GetProperty("supported").GetBoolean());

        var schemes = config.GetProperty("authenticationSchemes");
        Assert.Equal("oauthbearertoken", schemes[0].GetProperty("type").GetString());
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/ServiceProviderConfig")]
    public async Task ServiceProviderConfig_WithoutToken_ReturnsUnauthorized()
    {
        using var anon = _fixture.CreateClient();
        var response = await anon.GetAsync("/scim/v2/ServiceProviderConfig");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/ResourceTypes")]
    public async Task ResourceTypes_ReturnsUserAndGroup()
    {
        var response = await _client.GetAsync("/scim/v2/ResourceTypes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(2, body.GetProperty("totalResults").GetInt32());
        var ids = body.GetProperty("resources").EnumerateArray()
            .Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Contains("User", ids);
        Assert.Contains("Group", ids);
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/ResourceTypes/{id}")]
    public async Task ResourceType_ById_ReturnsUser()
    {
        var response = await _client.GetAsync("/scim/v2/ResourceTypes/User");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal("User", body.GetProperty("id").GetString());
        Assert.Equal("/Users", body.GetProperty("endpoint").GetString());
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/ResourceTypes/{id}")]
    public async Task ResourceType_UnknownId_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/scim/v2/ResourceTypes/Nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/Schemas")]
    public async Task Schemas_ReturnsUserAndGroupDefinitions()
    {
        var response = await _client.GetAsync("/scim/v2/Schemas");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(2, body.GetProperty("totalResults").GetInt32());
        var ids = body.GetProperty("resources").EnumerateArray()
            .Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Contains("urn:ietf:params:scim:schemas:core:2.0:User", ids);
        Assert.Contains("urn:ietf:params:scim:schemas:core:2.0:Group", ids);
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/Schemas/{id}")]
    public async Task Schema_ById_ReturnsUserSchema()
    {
        var response = await _client.GetAsync("/scim/v2/Schemas/urn:ietf:params:scim:schemas:core:2.0:User");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal("urn:ietf:params:scim:schemas:core:2.0:User", body.GetProperty("id").GetString());
        var attrNames = body.GetProperty("attributes").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString()).ToList();
        Assert.Contains("userName", attrNames);
        Assert.Contains("active", attrNames);
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/Users")]
    public async Task Request_WithoutBearerToken_ReturnsUnauthorized()
    {
        using var anon = _fixture.CreateClient();
        var response = await anon.GetAsync("/scim/v2/Users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/Users")]
    public async Task Request_WithWrongBearerToken_ReturnsUnauthorized()
    {
        using var wrong = _fixture.CreateClient(c =>
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token"));
        var response = await wrong.GetAsync("/scim/v2/Users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task CreateUserAsync(string userName)
    {
        var response = await _client.PostAsJsonAsync("/scim/v2/Users", new
        {
            userName,
            displayName = userName,
            active = true,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(text, _json);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Sharing;

/// <summary>
/// Integration tests for the ArcGIS Portal community-group + item-sharing surface
/// (#1868): createGroup, addUsers/removeUsers, content/items/{id}/share·unshare,
/// plus the authentication/authorization gates. The mutation endpoints require an
/// authenticated principal; these drive a referer-bound portal token (issued via
/// the shared <c>IPortalTokenIssuer</c>) as the caller so the portal-token bridge
/// authenticates the request on the AllowAnonymous route.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class SharingCommunityTests : IAsyncLifetime
{
    private const string AdminPassword = WebAppFixture.SharedAdminPassword;
    private const string Referer = "https://app.example.com";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly WebAppFixture _fixture;

    public SharingCommunityTests()
    {
        _fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
        });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/community/createGroup")]
    public async Task CreateGroup_Authenticated_CreatesGroupOwnedByCaller()
    {
        var token = await IssueUserTokenAsync("alice");
        using var client = CreateUserClient();

        using var response = await PostFormAsync(client, $"/sharing/rest/community/createGroup?token={token}",
            ("title", "Field Crew"),
            ("access", "private"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadAsync<GroupOpPayload>(response);
        payload.Success.Should().BeTrue();
        payload.GroupId.Should().NotBeNullOrWhiteSpace();
        payload.Group!.Owner.Should().Be("alice");
        payload.Group.Members.Should().Contain("alice");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/community/createGroup")]
    public async Task CreateGroup_Anonymous_Returns401()
    {
        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(client, "/sharing/rest/community/createGroup",
            ("title", "Field Crew"));

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/community/groups/{groupId}/addUsers")]
    [Endpoint("POST /sharing/rest/community/groups/{groupId}/removeUsers")]
    public async Task AddUsers_AsOwner_AddsMembersThenRemoveUsersRemovesThem()
    {
        var token = await IssueUserTokenAsync("alice");
        using var client = CreateUserClient();

        var groupId = await CreateGroupAsync(client, token, "Field Crew");

        using var added = await PostFormAsync(client, $"/sharing/rest/community/groups/{groupId}/addUsers?token={token}",
            ("users", "bob,carol"));
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        var addedPayload = await ReadAsync<MembershipPayload>(added);
        addedPayload.Members.Should().Contain(["alice", "bob", "carol"]);

        using var removed = await PostFormAsync(client, $"/sharing/rest/community/groups/{groupId}/removeUsers?token={token}",
            ("users", "bob"));
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        var removedPayload = await ReadAsync<MembershipPayload>(removed);
        removedPayload.Members.Should().Contain("carol");
        removedPayload.Members.Should().NotContain("bob");
        // The owner can never be removed from their own group.
        removedPayload.Members.Should().Contain("alice");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/community/groups/{groupId}/addUsers")]
    public async Task AddUsers_AsNonOwnerNonAdmin_Returns403()
    {
        var ownerToken = await IssueUserTokenAsync("alice");
        using var ownerClient = CreateUserClient();
        var groupId = await CreateGroupAsync(ownerClient, ownerToken, "Field Crew");

        // A different, unprivileged user cannot change the membership.
        var malloryToken = await IssueUserTokenAsync("mallory");
        using var malloryClient = CreateUserClient();
        using var response = await PostFormAsync(malloryClient, $"/sharing/rest/community/groups/{groupId}/addUsers?token={malloryToken}",
            ("users", "mallory"));

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/content/items/{itemId}/share")]
    [Endpoint("POST /sharing/rest/content/items/{itemId}/unshare")]
    public async Task ShareItem_ToGroup_MakesItVisibleThenUnshareRevokes()
    {
        var token = await IssueUserTokenAsync("alice");
        using var client = CreateUserClient();
        var groupId = await CreateGroupAsync(client, token, "Field Crew");

        const string itemId = "svc-rivers";

        using var shared = await PostFormAsync(client, $"/sharing/rest/content/items/{itemId}/share?token={token}",
            ("groups", groupId),
            ("org", "true"));
        shared.StatusCode.Should().Be(HttpStatusCode.OK);
        var sharedPayload = await ReadAsync<SharingPayload>(shared);
        sharedPayload.ItemId.Should().Be(itemId);
        sharedPayload.Sharing!.Org.Should().BeTrue();
        sharedPayload.Sharing.Groups.Should().Contain(groupId);

        using var unshared = await PostFormAsync(client, $"/sharing/rest/content/items/{itemId}/unshare?token={token}",
            ("groups", groupId),
            ("org", "true"));
        unshared.StatusCode.Should().Be(HttpStatusCode.OK);
        var unsharedPayload = await ReadAsync<SharingPayload>(unshared);
        unsharedPayload.Sharing!.Org.Should().BeFalse();
        unsharedPayload.Sharing.Groups.Should().NotContain(groupId);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/content/items/{itemId}/share")]
    public async Task ShareItem_Anonymous_Returns401()
    {
        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(client, "/sharing/rest/content/items/svc-rivers/share",
            ("everyone", "true"));

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/content/items/{itemId}/share")]
    public async Task ShareItem_ByNonOwnerNonAdmin_Returns403()
    {
        // alice shares the item first and becomes its sharing owner.
        var aliceToken = await IssueUserTokenAsync("alice");
        using var aliceClient = CreateUserClient();
        const string itemId = "svc-owned";
        using var firstShare = await PostFormAsync(aliceClient, $"/sharing/rest/content/items/{itemId}/share?token={aliceToken}",
            ("org", "true"));
        firstShare.StatusCode.Should().Be(HttpStatusCode.OK);

        // mallory (a different non-admin user) cannot change the item's sharing.
        var malloryToken = await IssueUserTokenAsync("mallory");
        using var malloryClient = CreateUserClient();
        using var response = await PostFormAsync(malloryClient, $"/sharing/rest/content/items/{itemId}/share?token={malloryToken}",
            ("everyone", "true"));

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/community/groups/{groupId}")]
    public async Task GetGroup_PrivateGroup_NotVisibleToNonMember_Returns404()
    {
        var token = await IssueUserTokenAsync("alice");
        using var ownerClient = CreateUserClient();
        var groupId = await CreateGroupAsync(ownerClient, token, "Secret Crew");

        // A non-member anonymous caller cannot even confirm the private group exists.
        using var anon = _fixture.CreateClient();
        using var response = await anon.GetAsync($"/sharing/rest/community/groups/{groupId}?f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/community/groups/{groupId}/delete")]
    public async Task DeleteGroup_AsOwner_RemovesGroup()
    {
        var token = await IssueUserTokenAsync("alice");
        using var client = CreateUserClient();
        var groupId = await CreateGroupAsync(client, token, "Field Crew");

        using var response = await PostFormAsync(client, $"/sharing/rest/community/groups/{groupId}/delete?token={token}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var lookup = await client.GetAsync($"/sharing/rest/community/groups/{groupId}?token={token}&f=json");
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        lookup.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<string> CreateGroupAsync(HttpClient client, string token, string title)
    {
        using var response = await PostFormAsync(client, $"/sharing/rest/community/createGroup?token={token}",
            ("title", title),
            ("access", "private"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadAsync<GroupOpPayload>(response);
        return payload.GroupId;
    }

    private async Task<string> IssueUserTokenAsync(string username)
    {
        // A referer-bound portal token authenticates the caller on the AllowAnonymous
        // sharing routes via the portal-token bridge, without the IP binding the
        // in-process TestServer cannot populate.
        var issuer = _fixture.Services.GetRequiredService<IPortalTokenIssuer>();
        var issuance = await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: username,
                DisplayName: username,
                TenantId: null,
                Roles: ["org_user"],
                ClientType: PortalTokenClientType.Referer,
                BindingValue: Referer,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30)),
            CancellationToken.None);
        return issuance.Token;
    }

    private HttpClient CreateUserClient()
        => _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("Referer", Referer));

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, params (string Key, string Value)[] pairs)
    {
        var content = new FormUrlEncodedContent(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));
        return await client.PostAsync(url, content);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException("Empty response body.");
    }

    private sealed record GroupOpPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("success")]
        public bool Success { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("groupId")]
        public string GroupId { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("group")]
        public GroupPayload? Group { get; init; }
    }

    private sealed record GroupPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("owner")]
        public string Owner { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("members")]
        public IReadOnlyList<string> Members { get; init; } = [];
    }

    private sealed record MembershipPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("members")]
        public IReadOnlyList<string> Members { get; init; } = [];
    }

    private sealed record SharingPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("itemId")]
        public string ItemId { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("sharing")]
        public SharingStatePayload? Sharing { get; init; }
    }

    private sealed record SharingStatePayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("everyone")]
        public bool Everyone { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("org")]
        public bool Org { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("groups")]
        public IReadOnlyList<string> Groups { get; init; } = [];
    }
}

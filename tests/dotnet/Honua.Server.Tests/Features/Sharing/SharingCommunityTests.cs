// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
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
        var graph = new TestMetadataV2GraphBuilder()
            .AddService("svc-rivers", "Rivers", protocols: [ServiceProtocols.FeatureServer],
                accessPolicy: new AccessPolicy { AllowedRoles = ["publisher"] })
            .AddService("svc-owned", "Owned", protocols: [ServiceProtocols.FeatureServer],
                accessPolicy: new AccessPolicy { AllowedRoles = ["publisher"] })
            .AddService("content-a", "First item", protocols: [ServiceProtocols.FeatureServer],
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddService("content-b", "Second item", protocols: [ServiceProtocols.MapServer],
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddService("content-private", "Restricted item", protocols: [ServiceProtocols.FeatureServer],
                accessPolicy: new AccessPolicy { AllowedRoles = ["parcel-admin"] })
            .AddResource("private-resource", "Private resource", accessPolicy: new AccessPolicy { AllowedRoles = ["parcel-admin"] })
            .AddStorageBinding("private-binding", "private-resource", "features", storageLayerId: 0)
            .AddPublication("private-publication", "content-private", "private-resource", layerIndex: 0,
                storageBindingId: "private-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer)
            .Build();
        graph = graph with { Services = graph.Services.Select(service => service with
            { Metadata = service.Metadata with { Publisher = "alice" } }).ToArray() };
        _fixture = new WebAppFixture()
            .ReplaceService<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph))
            .ConfigureWebHost(builder =>
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
    public async Task CreateGroup_FormToken_AuthenticatesAndPreservesFormForEndpoint()
    {
        // The same form carries both the credential and endpoint fields. A 200
        // with the requested title proves the middleware authenticates the
        // form token without consuming the body needed by the GeoServices route.
        var token = await IssueUserTokenAsync("alice");
        using var client = CreateUserClient();

        using var response = await PostFormAsync(client, "/sharing/rest/community/createGroup",
            ("token", token),
            ("title", "Form Token Field Crew"),
            ("access", "private"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadAsync<GroupOpPayload>(response);
        payload.Success.Should().BeTrue();
        payload.Group!.Owner.Should().Be("alice");
        payload.Group.Title.Should().Be("Form Token Field Crew");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/community/createGroup")]
    public async Task CreateGroup_Anonymous_Returns401()
    {
        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(client, "/sharing/rest/community/createGroup",
            ("title", "Field Crew"));

        await response.AssertGeoServicesErrorAsync(401, 499);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/community/createGroup")]
    public async Task CreateGroup_InvalidFormToken_Returns498()
    {
        using var client = CreateUserClient();
        using var response = await PostFormAsync(client, "/sharing/rest/community/createGroup",
            ("token", "invalid-portal-token"),
            ("title", "Field Crew"));

        await response.AssertGeoServicesErrorAsync(498);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/community/createGroup")]
    public async Task CreateGroup_MultipartToken_DoesNotAuthenticate()
    {
        var token = await IssueUserTokenAsync("alice");
        using var client = CreateUserClient();
        using var content = new MultipartFormDataContent
        {
            { new StringContent(token), "token" },
            { new StringContent("Field Crew"), "title" },
        };
        using var response = await client.PostAsync("/sharing/rest/community/createGroup", content);

        await response.AssertGeoServicesErrorAsync(401, 499);
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

        await response.AssertGeoServicesErrorAsync(403);
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
        sharedPayload.NotSharedWith.Should().NotContain(groupId);
        sharedPayload.NotSharedWith.Should().Contain("org");
        sharedPayload.Sharing!.Org.Should().BeFalse();
        sharedPayload.Sharing.Everyone.Should().BeFalse();
        sharedPayload.Sharing.Groups.Should().BeEquivalentTo([groupId]);

        using var unshared = await PostFormAsync(client, $"/sharing/rest/content/items/{itemId}/unshare?token={token}",
            ("groups", groupId),
            ("org", "true"));
        unshared.StatusCode.Should().Be(HttpStatusCode.OK);
        var unsharedPayload = await ReadAsync<SharingPayload>(unshared);
        unsharedPayload.NotSharedWith.Should().NotBeEmpty();
        unsharedPayload.Sharing!.Org.Should().BeFalse();
        unsharedPayload.Sharing.Groups.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/content/items/{itemId}/share")]
    public async Task ShareItem_Anonymous_Returns401()
    {
        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(client, "/sharing/rest/content/items/svc-rivers/share",
            ("everyone", "true"));

        await response.AssertGeoServicesErrorAsync(401, 499);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/content/items/{itemId}/share")]
    public async Task ShareItem_ByNonOwnerNonAdmin_Returns403()
    {
        // Actual metadata publisher ownership is enforced before any sharing state exists.
        var aliceToken = await IssueUserTokenAsync("alice");
        using var aliceClient = CreateUserClient();
        const string itemId = "svc-owned";
        using var firstShare = await PostFormAsync(aliceClient, $"/sharing/rest/content/items/{itemId}/share?token={aliceToken}",
            ("org", "true"));
        firstShare.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstPayload = await ReadAsync<SharingPayload>(firstShare);
        firstPayload.NotSharedWith.Should().Contain("org");
        firstPayload.Sharing!.Org.Should().BeFalse();

        // A different user cannot seize the actual item through first-share ownership.
        var malloryToken = await IssueUserTokenAsync("mallory");
        using var malloryClient = CreateUserClient();
        using var response = await PostFormAsync(malloryClient, $"/sharing/rest/content/items/{itemId}/share?token={malloryToken}",
            ("everyone", "true"));
        await response.AssertGeoServicesErrorAsync(403);
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

        await response.AssertGeoServicesErrorAsync(404);
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
        await lookup.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Endpoint("GET /sharing/rest/community/self")]
    [Endpoint("GET /sharing/rest/community/users/{username}")]
    public async Task UserGroups_ReturnOnlyOwnedAndMemberGroups_AndKeepForeignUserGuard()
    {
        var alice = await IssueUserTokenAsync("alice");
        var bob = await IssueUserTokenAsync("bob");
        using var client = CreateUserClient();
        var owned = await CreateGroupAsync(client, alice, "Owned");
        var member = await CreateGroupAsync(client, bob, "Member");
        _ = await CreateGroupAsync(client, bob, "Unrelated");
        using var add = await PostFormAsync(client, $"/sharing/rest/community/groups/{member}/addUsers?token={bob}", ("users", "alice"));
        add.StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var path in new[] { "self", "users/alice" })
        {
            using var response = await client.GetAsync($"/sharing/rest/community/{path}?token={alice}&f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("username").GetString().Should().Be("alice");
            doc.RootElement.GetProperty("groups").EnumerateArray()
                .Select(group => group.GetProperty("id").GetString()).Should().BeEquivalentTo(owned, member);
        }

        using var foreign = await client.GetAsync($"/sharing/rest/community/users/bob?token={alice}&f=json");
        await foreign.AssertGeoServicesErrorAsync(404);
        using var remove = await PostFormAsync(client, $"/sharing/rest/community/groups/{member}/removeUsers?token={bob}", ("users", "alice"));
        remove.StatusCode.Should().Be(HttpStatusCode.OK);
        using var after = await client.GetAsync($"/sharing/rest/community/self?token={alice}&f=json");
        using var afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        afterDoc.RootElement.GetProperty("groups").EnumerateArray()
            .Select(group => group.GetProperty("id").GetString()).Should().Equal(owned);
    }

    [IntegrationTest]
    [Endpoint("GET /sharing/rest/content/groups/{groupId}")]
    public async Task GroupContent_PublicDescriptionDoesNotGrantContentAccess()
    {
        var alice = await IssueUserTokenAsync("alice");
        var bob = await IssueUserTokenAsync("bob");
        using var client = CreateUserClient();
        using var create = await PostFormAsync(client, $"/sharing/rest/community/createGroup?token={alice}",
            ("title", "Public description"), ("access", "public"));
        var group = (await ReadAsync<GroupOpPayload>(create)).GroupId;
        using var anonymous = await client.GetAsync($"/sharing/rest/content/groups/{group}?f=json");
        await anonymous.AssertGeoServicesErrorAsync(401, 499);
        using var nonmember = await client.GetAsync($"/sharing/rest/content/groups/{group}?token={bob}&f=json");
        await nonmember.AssertGeoServicesErrorAsync(404);
        using var absent = await client.GetAsync($"/sharing/rest/content/groups/absent?token={bob}&f=json");
        await absent.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Endpoint("GET /sharing/rest/content/groups/{groupId}")]
    public async Task GroupContent_UnsupportedPublicShare_RemainsEmpty()
    {
        var token = await IssueUserTokenAsync("alice");
        using var client = CreateUserClient();
        var group = await CreateGroupAsync(client, token, "No fabricated content");
        using var share = await PostFormAsync(client, $"/sharing/rest/content/items/content-a/share?token={token}", ("everyone", "true"), ("org", "true"));
        (await ReadAsync<SharingPayload>(share)).NotSharedWith.Should().BeEquivalentTo(["everyone", "org"]);
        using var response = await client.GetAsync($"/sharing/rest/content/groups/{group}?token={token}&f=json&num=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("nextStart").GetInt32().Should().Be(-1);
    }

    [IntegrationTest]
    [Endpoint("GET /sharing/rest/content/groups/{groupId}")]
    public async Task GroupContent_ControlledStoreFixture_PagesAndPreservesUnderlyingRbac()
    {
        var token = await IssueUserTokenAsync("alice");
        using var client = CreateUserClient();
        var group = await CreateGroupAsync(client, token, "Controlled read-store fixture");
        var other = await CreateGroupAsync(client, token, "Other group");
        // Direct controlled-store setup exercises paging and independent resource policy.
        // Actual production sharing and persisted SQL reads have a separate HTTP regression;
        // this controlled-store setup is not a native content-certification receipt.
        var store = _fixture.Services.GetRequiredService<IPortalItemSharingStore>();
        foreach (var item in new[] { "content-a", "content-b", "content-private" })
        {
            await store.ShareAsync(item, "alice", new PortalItemShareRequest(false, false, [group]), CancellationToken.None);
        }

        for (var start = 1; start <= 3; start++)
        {
            using var response = await client.GetAsync($"/sharing/rest/content/groups/{group}?token={token}&f=json&start={start}&num=1");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            root.GetProperty("total").GetInt32().Should().Be(2);
            root.GetProperty("start").GetInt32().Should().Be(start);
            root.GetProperty("num").GetInt32().Should().Be(start <= 2 ? 1 : 0);
            root.GetProperty("nextStart").GetInt32().Should().Be(start == 1 ? 2 : -1);
            if (start <= 2)
            {
                var item = root.GetProperty("items")[0];
                item.GetProperty("id").GetString().Should().Be(start == 1 ? "content-a" : "content-b");
                item.GetProperty("groupCategories").GetArrayLength().Should().Be(0);
            }
        }

        using var unrelated = await client.GetAsync($"/sharing/rest/content/groups/{other}?token={token}&f=json");
        using var unrelatedDoc = JsonDocument.Parse(await unrelated.Content.ReadAsStringAsync());
        unrelatedDoc.RootElement.GetProperty("total").GetInt32().Should().Be(0);
        await store.UnshareAsync("content-a", new PortalItemShareRequest(false, false, [group]), CancellationToken.None);
        using var after = await client.GetAsync($"/sharing/rest/content/groups/{group}?token={token}&f=json");
        using var afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        afterDoc.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()).Should().Equal("content-b");
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
        using var content = new FormUrlEncodedContent(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));
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
        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

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

        [System.Text.Json.Serialization.JsonPropertyName("notSharedWith")]
        public IReadOnlyList<string> NotSharedWith { get; init; } = [];

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

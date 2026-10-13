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
using Npgsql;

namespace Honua.Server.Tests.Features.Sharing;

/// <summary>Exercises real group sharing against persisted FeatureServer data and canonical denial paths.</summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
public sealed class PortalCanonicalGroupSharingTests : IAsyncLifetime
{
    private const string Referer = "https://app.example.com";
    private const string ItemId = "svc-test-feature";
    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
    });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var graph = snapshot.Graph with
        {
            Services = snapshot.Graph.Services.Where(service => service.Metadata.Id == ItemId)
                .Select(service => service with
                {
                    Metadata = service.Metadata with { Publisher = "alice" },
                    AccessPolicy = new AccessPolicy { AllowedRoles = ["publisher"], AllowedWriteRoles = ["publisher"] },
                }).ToArray(),
            Publications = snapshot.Graph.Publications.Where(publication => publication.ServiceId == ItemId).ToArray(),
            Resources = snapshot.Graph.Resources.Select(resource => resource with
            {
                AccessPolicy = new AccessPolicy { AllowedWriteRoles = ["publisher"] },
            }).ToArray(),
        };
        graph.Services.Should().ContainSingle();
        graph.Publications.Should().NotBeEmpty();
        var provider = (TestMetadataV2GraphProvider)_fixture.Services.GetRequiredService<IMetadataV2GraphProvider>();
        provider.SetGraph(graph, schema: _fixture.CurrentSchema);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/content/items/{itemId}/share")]
    [Endpoint("POST /sharing/rest/content/items/{itemId}/unshare")]
    [Endpoint("GET /sharing/rest/content/groups/{groupId}")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task GroupShare_ActualHttpReadMatchesSql_ThenMembershipAndUnshareRevoke()
    {
        using var client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("Referer", Referer));
        var owner = await IssueAsync("alice", "publisher");
        var member = await IssueAsync("bob", "org_user");
        var outsider = await IssueAsync("carol", "org_user");
        var group = await CreateGroupAsync(client, owner);
        using (var add = await PostAsync(client, $"/sharing/rest/community/groups/{group}/addUsers?token={owner}", ("users", "bob")))
        {
            add.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        await AssertQueryDeniedAsync(client, member);

        using (var share = await PostAsync(client, $"/sharing/rest/content/items/{ItemId}/share?token={owner}", ("groups", group)))
        {
            var document = await DocumentAsync(share);
            document.GetProperty("notSharedWith").GetArrayLength().Should().Be(0);
            document.GetProperty("sharing").GetProperty("groups")[0].GetString().Should().Be(group);
        }
        await AssertQueryMatchesSqlAsync(client, member);
        await AssertMemberWriteDeniedAndSqlUnchangedAsync(client, member);
        await AssertQueryDeniedAsync(client, outsider);
        using (var content = await client.GetAsync($"/sharing/rest/content/groups/{group}?token={member}&f=json"))
        {
            var document = await DocumentAsync(content);
            document.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString())
                .Should().Contain(ItemId);
        }
        using (var remove = await PostAsync(client, $"/sharing/rest/community/groups/{group}/removeUsers?token={owner}", ("users", "bob")))
        {
            remove.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        await AssertQueryDeniedAsync(client, member);
        await AssertGroupContentAbsentAsync(client, member, group, nonmember: true);
        using (var add = await PostAsync(client, $"/sharing/rest/community/groups/{group}/addUsers?token={owner}", ("users", "bob")))
        {
            add.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        await AssertQueryMatchesSqlAsync(client, member);
        using (var unshare = await PostAsync(client, $"/sharing/rest/content/items/{ItemId}/unshare?token={owner}", ("groups", group)))
        {
            var document = await DocumentAsync(unshare);
            document.GetProperty("sharing").GetProperty("groups").GetArrayLength().Should().Be(0);
        }
        await AssertQueryDeniedAsync(client, member);
        await AssertGroupContentAbsentAsync(client, member, group, nonmember: false);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/content/items/{itemId}/share")]
    public async Task Share_RejectsForeignPublisherMissingItemAndIneligibleGroup_AndKeepsPublicRefusal()
    {
        using var client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("Referer", Referer));
        var owner = await IssueAsync("alice", "publisher");
        var stranger = await IssueAsync("mallory", "org_user");
        var ownGroup = await CreateGroupAsync(client, owner);
        var foreignGroup = await CreateGroupAsync(client, stranger);
        using (var foreign = await PostAsync(client, $"/sharing/rest/content/items/{ItemId}/share?token={stranger}", ("groups", ownGroup)))
        {
            await foreign.AssertGeoServicesErrorAsync(403);
        }
        using (var missing = await PostAsync(client, $"/sharing/rest/content/items/absent/share?token={owner}", ("groups", ownGroup)))
        {
            await missing.AssertGeoServicesErrorAsync(404);
        }
        using var refused = await PostAsync(client, $"/sharing/rest/content/items/{ItemId}/share?token={owner}",
            ("groups", foreignGroup), ("everyone", "true"), ("org", "true"));
        var document = await DocumentAsync(refused);
        document.GetProperty("notSharedWith").EnumerateArray().Select(value => value.GetString())
            .Should().BeEquivalentTo([foreignGroup, "everyone", "org"]);
        var state = document.GetProperty("sharing");
        state.GetProperty("groups").GetArrayLength().Should().Be(0);
        state.GetProperty("everyone").GetBoolean().Should().BeFalse();
        state.GetProperty("org").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/community/groups/{groupId}/addUsers")]
    [Endpoint("POST /sharing/rest/community/groups/{groupId}/removeUsers")]
    public async Task TenantGroup_OwnerSubjectInAnotherTenantCannotInviteOrRemoveMembers()
    {
        using var client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("Referer", Referer));
        var tenantA = await IssueAsync("alice", "publisher", "tenant-a");
        var tenantB = await IssueAsync("alice", "publisher", "tenant-b");
        var unscoped = await IssueAsync("alice", "publisher");
        var group = await CreateGroupAsync(client, tenantA);
        foreach (var token in new[] { tenantB, unscoped })
        {
            foreach (var operation in new[] { "addUsers", "removeUsers" })
            {
                using var refused = await PostAsync(client, $"/sharing/rest/community/groups/{group}/{operation}?token={token}", ("users", "bob"));
                await refused.AssertGeoServicesErrorAsync(404);
            }
        }
        using var allowed = await PostAsync(client, $"/sharing/rest/community/groups/{group}/addUsers?token={tenantA}", ("users", "bob"));
        (await DocumentAsync(allowed)).GetProperty("members").EnumerateArray()
            .Select(member => member.GetString()).Should().Contain("bob");
    }

    private async Task AssertMemberWriteDeniedAndSqlUnchangedAsync(HttpClient client, string token)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = new NpgsqlCommand(
            "SELECT jsonb_agg(jsonb_build_array(objectid, attributes, ST_AsEWKB(geometry)::text) ORDER BY objectid)::text FROM features WHERE layer_id = 0",
            connection);
        var before = (string)(await command.ExecuteScalarAsync())!;
        using var witness = JsonDocument.Parse(before);
        var objectId = witness.RootElement[0][0].GetInt64();
        var updates = JsonSerializer.Serialize(new[] { new { attributes = new { objectid = objectId, name = "refused group-member update" } } });
        using var response = await PostAsync(client, $"/rest/services/test/FeatureServer/0/applyEdits?token={token}",
            ("f", "json"), ("updates", updates));
        await response.AssertGeoServicesErrorAsync(403);
        ((string)(await command.ExecuteScalarAsync())!).Should().Be(before,
            "group read sharing must not change persisted attributes or geometry through edits");
    }

    private static async Task AssertGroupContentAbsentAsync(HttpClient client, string token, string group, bool nonmember)
    {
        using var response = await client.GetAsync($"/sharing/rest/content/groups/{group}?token={token}&f=json");
        if (nonmember)
        {
            await response.AssertGeoServicesErrorAsync(404);
        }
        else
        {
            (await DocumentAsync(response)).GetProperty("items").GetArrayLength().Should().Be(0);
        }
    }

    private async Task AssertQueryMatchesSqlAsync(HttpClient client, string token)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM features WHERE layer_id = 0", connection);
        var expected = Convert.ToInt64(await command.ExecuteScalarAsync());
        expected.Should().BeGreaterThan(0, "the read oracle must exercise actual persisted features");
        using var response = await client.GetAsync($"/rest/services/test/FeatureServer/0/query?f=json&where=1%3D1&returnCountOnly=true&token={token}");
        var document = await DocumentAsync(response);
        document.TryGetProperty("error", out _).Should().BeFalse();
        document.GetProperty("count").GetInt64().Should().Be(expected);
    }

    private static async Task AssertQueryDeniedAsync(HttpClient client, string token)
    {
        using var response = await client.GetAsync($"/rest/services/test/FeatureServer/0/query?f=json&where=1%3D1&returnCountOnly=true&token={token}");
        await response.AssertGeoServicesErrorAsync(403);
    }

    private async Task<string> IssueAsync(string username, string role, string? tenantId = null)
    {
        var issuer = _fixture.Services.GetRequiredService<IPortalTokenIssuer>();
        var issuance = await issuer.IssueAsync(new PortalTokenIssueRequest(username, username, tenantId, [role],
            PortalTokenClientType.Referer, Referer, DateTimeOffset.UtcNow.AddMinutes(30)), CancellationToken.None);
        return issuance.Token;
    }

    private static async Task<string> CreateGroupAsync(HttpClient client, string token)
    {
        using var response = await PostAsync(client, $"/sharing/rest/community/createGroup?token={token}",
            ("title", "owned test group"), ("access", "private"));
        return (await DocumentAsync(response)).GetProperty("groupId").GetString()!;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, params (string Key, string Value)[] values)
    {
        using var content = new FormUrlEncodedContent(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value)));
        return await client.PostAsync(url, content);
    }

    private static async Task<JsonElement> DocumentAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

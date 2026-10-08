// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Console;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.GetMetadata)]
public sealed class ProjectedCatalogDiscoveryEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/console/catalog-endpoints/{workspaceId}")]
    [Endpoint("GET /api/v1/console/catalog-endpoints/{workspaceId}/{endpointKey}")]
    [Endpoint("GET /api/v1/console/catalog-endpoints/{workspaceId}/{endpointKey}/items/{itemId}")]
    public async Task HostedProjection_RespectsMappingAdminAuthorizationAndCanonicalDirectory()
    {
        var graph = ProjectedCatalogDiscoveryRegistryStoreTests.Graph();
        var provider = new TestMetadataV2GraphProvider(graph);
        await using var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "catalog-live-projection-key");
                builder.UseSetting("Console:CatalogDiscovery:Workspaces:0:Id", "default");
                builder.UseSetting("Console:CatalogDiscovery:Workspaces:0:TenantId", "public");
                builder.UseSetting("Console:CatalogDiscovery:Workspaces:0:Namespace", "alpha");
                builder.UseSetting("Console:CatalogDiscovery:Workspaces:1:Id", "other-tenant");
                builder.UseSetting("Console:CatalogDiscovery:Workspaces:1:TenantId", "other");
                builder.UseSetting("Console:CatalogDiscovery:Workspaces:1:Namespace", "alpha");
                builder.UseSetting("Console:CatalogDiscovery:Workspaces:2:Id", "empty");
                builder.UseSetting("Console:CatalogDiscovery:Workspaces:2:TenantId", "public");
                builder.UseSetting("Console:CatalogDiscovery:Workspaces:2:Namespace", "beta");
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton<IMetadataV2GraphProvider>(provider);
                services.AddSingleton<IMetadataV2GraphStore>(provider);
            });
        await fixture.InitializeAsync();
        using var admin = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", "catalog-live-projection-key"));
        using var anonymous = fixture.CreateClient();
        foreach (var route in new[] { "default", "default/esri", "default/esri/items/unknown" })
        {
            using var denied = await anonymous.GetAsync("/api/v1/console/catalog-endpoints/" + route);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        foreach (var workspace in new[] { "unknown", "other-tenant" })
        {
            using var absent = await admin.GetAsync("/api/v1/console/catalog-endpoints/" + workspace);
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        }

        using var emptyResponse = await admin.GetAsync("/api/v1/console/catalog-endpoints/empty");
        Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
        using var empty = JsonDocument.Parse(await emptyResponse.Content.ReadAsStringAsync());
        Assert.Empty(empty.RootElement.GetProperty("data").GetProperty("endpoints").EnumerateArray());

        using var directoryResponse = await admin.GetAsync("/rest/services?f=json");
        Assert.Equal(HttpStatusCode.OK, directoryResponse.StatusCode);
        using var directory = JsonDocument.Parse(await directoryResponse.Content.ReadAsStringAsync());
        var directoryEntries = directory.RootElement.GetProperty("services").EnumerateArray().ToArray();
        var canonical = directoryEntries
            .Where(entry => entry.GetProperty("type").GetString() is "FeatureServer" or "MapServer")
            .Select(entry => (Name: entry.GetProperty("name").GetString(),
                Type: entry.GetProperty("type").GetString(), Url: entry.GetProperty("url").GetString()))
            .OrderBy(entry => entry.Url).ToArray();
        Assert.Equal(2, canonical.Length);
        using var detailResponse = await admin.GetAsync("/api/v1/console/catalog-endpoints/default/esri");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var items = detail.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(canonical.Select(entry => (entry.Name, entry.Url)), items
            .Select(item => (Name: item.GetProperty("title").GetString(), Url: item.GetProperty("resource").GetString()))
            .OrderBy(item => item.Url));
        var excludedUrls = directoryEntries
            .Where(entry => entry.GetProperty("type").GetString() is not ("FeatureServer" or "MapServer"))
            .Select(entry => entry.GetProperty("url").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(items, item => excludedUrls.Contains(item.GetProperty("resource").GetString()));
        foreach (var item in items)
        {
            using var itemResponse = await admin.GetAsync("/api/v1/console/catalog-endpoints/default/esri/items/" + item.GetProperty("id").GetString());
            Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
            using var itemPayload = JsonDocument.Parse(await itemResponse.Content.ReadAsStringAsync());
            var protocol = itemPayload.RootElement.GetProperty("data").GetProperty("groups").EnumerateArray()
                .SelectMany(group => group.GetProperty("fields").EnumerateArray())
                .Single(field => field.GetProperty("label").GetString() == "Protocol").GetProperty("value").GetString();
            Assert.Equal(canonical.Single(entry => entry.Url == item.GetProperty("resource").GetString()).Type, protocol);
        }
    }
}

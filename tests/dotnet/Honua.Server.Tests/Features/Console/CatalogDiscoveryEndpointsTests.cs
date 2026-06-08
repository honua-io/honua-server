// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Console.Services;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Console;

/// <summary>
/// Integration tests for the Console catalog discovery-endpoints registry read
/// API (honua-server#1279): registry list (incl. empty workspace and
/// opt-in-vs-default-on aggregates), endpoint detail (incl. not-found), item
/// drill-down (incl. not-found), and admin permission enforcement.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.RoleManagement)]
public sealed class CatalogDiscoveryEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "catalog-discovery-admin-key";
    private const string WorkspaceId = "default";
    private const string EmptyWorkspaceId = "empty-ws";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: true) },
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _adminClient = null!;
    private HttpClient _anonymousClient = null!;

    public CatalogDiscoveryEndpointsTests()
    {
        // Seed two workspaces: the canonical "default" (five dialects, mixed
        // auto-default/opt-in), and an "empty-ws" with no published dialects so
        // the empty-registry contract is exercised.
        var seeds = new[]
        {
            new CatalogDiscoveryWorkspaceSeed
            {
                WorkspaceId = WorkspaceId,
                WorkspaceName = "Default workspace",
                PublicHost = "https://example.test",
                Endpoints =
                [
                    new CatalogEndpointSeed
                    {
                        Endpoint = new CatalogEndpoint
                        {
                            Key = "esri",
                            Title = "Esri catalog",
                            Dialect = CatalogDialects.Esri,
                            Enabled = true,
                            AutoDefault = true,
                            Url = "/rest/portal",
                            Entries = 1,
                            Feeders = [new CatalogFeeder { Kind = "feature-server", Label = "Parcels FeatureServer" }],
                            IssueCount = 0,
                        },
                        LastRebuild = "2026-05-01T00:00:00Z",
                        Items =
                        [
                            new CatalogItemSeed
                            {
                                Row = new CatalogEndpointItem
                                {
                                    Id = "parcels",
                                    Title = "Parcels",
                                    FromService = "Parcels FeatureServer",
                                    Tags = ["parcels"],
                                    IssueCount = 0,
                                },
                                Detail = new CatalogItem
                                {
                                    Id = "parcels",
                                    Title = "Parcels",
                                    AutoMirror = true,
                                    Live = true,
                                    BackingServiceCount = 1,
                                    TagCount = 1,
                                    IssueCount = 0,
                                    Groups =
                                    [
                                        new CatalogItemFieldGroup
                                        {
                                            Title = "Identity",
                                            Scope = "resource-derived",
                                            Fields =
                                            [
                                                new CatalogItemField { Label = "Item id", State = CatalogFieldStates.System, Value = "parcels" },
                                            ],
                                        },
                                    ],
                                },
                            },
                        ],
                    },
                    new CatalogEndpointSeed
                    {
                        Endpoint = new CatalogEndpoint
                        {
                            Key = "odata",
                            Title = "OData v4",
                            Dialect = CatalogDialects.ODataV4,
                            Enabled = false,
                            AutoDefault = false,
                            Url = "/odata",
                            Entries = 0,
                            IssueCount = 0,
                        },
                        Items = [],
                    },
                ],
            },
            new CatalogDiscoveryWorkspaceSeed
            {
                WorkspaceId = EmptyWorkspaceId,
                WorkspaceName = "Empty workspace",
                Endpoints = [],
            },
        };

        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            })
            .ReplaceService<ICatalogDiscoveryRegistryStore>(new ConfigCatalogDiscoveryRegistryStore(seeds));
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _adminClient = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        _anonymousClient = _fixture.CreateClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/catalog-endpoints/{workspaceId}")]
    public async Task GetRegistry_ReturnsEndpointsWithAutoDefaultAndOptInAggregates()
    {
        var response = await _adminClient.GetAsync($"/api/v1/console/catalog-endpoints/{WorkspaceId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var registry = await ReadDataAsync<CatalogDiscoveryRegistry>(response);

        Assert.Equal(WorkspaceId, registry.WorkspaceId);
        Assert.Equal("https://example.test", registry.PublicHost);
        Assert.Equal(2, registry.Endpoints.Count);

        var esri = Assert.Single(registry.Endpoints, e => e.Key == "esri");
        Assert.True(esri.Enabled);
        Assert.True(esri.AutoDefault);
        Assert.Equal(CatalogDialects.Esri, esri.Dialect);
        Assert.Single(esri.Feeders);

        var odata = Assert.Single(registry.Endpoints, e => e.Key == "odata");
        Assert.False(odata.Enabled);
        Assert.False(odata.AutoDefault);

        // One auto-default-on (esri), one opt-in (odata).
        Assert.Equal(1, registry.AutoDefaultCount);
        Assert.Equal(1, registry.OptInCount);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/catalog-endpoints/{workspaceId}")]
    public async Task GetRegistry_ForEmptyWorkspace_ReturnsEmptyEndpointList()
    {
        var response = await _adminClient.GetAsync($"/api/v1/console/catalog-endpoints/{EmptyWorkspaceId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var registry = await ReadDataAsync<CatalogDiscoveryRegistry>(response);
        Assert.Equal(EmptyWorkspaceId, registry.WorkspaceId);
        Assert.Empty(registry.Endpoints);
        Assert.Equal(0, registry.AutoDefaultCount);
        Assert.Equal(0, registry.OptInCount);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/catalog-endpoints/{workspaceId}")]
    public async Task GetRegistry_ForUnconfiguredWorkspace_ReturnsEmptyRegistryNot404()
    {
        // A workspace with no configured discovery endpoints — including a
        // fresh/unseeded deployment where no workspace is seeded at all — is not
        // an error: the honest answer to "which dialects does this workspace
        // publish?" is "none". The registry read returns an empty-but-valid 200
        // so the Console renders an honest empty state instead of misreading a
        // 404 as "contract not bound". (Drill-down sub-resources still 404.)
        const string unconfiguredWorkspace = "never-seeded";
        var response = await _adminClient.GetAsync($"/api/v1/console/catalog-endpoints/{unconfiguredWorkspace}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var registry = await ReadDataAsync<CatalogDiscoveryRegistry>(response);
        Assert.Equal(unconfiguredWorkspace, registry.WorkspaceId);
        Assert.Empty(registry.Endpoints);
        Assert.Equal(0, registry.AutoDefaultCount);
        Assert.Equal(0, registry.OptInCount);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/catalog-endpoints/{workspaceId}/{endpointKey}")]
    public async Task GetEndpoint_ReturnsDetailWithMirroredItems()
    {
        var response = await _adminClient.GetAsync($"/api/v1/console/catalog-endpoints/{WorkspaceId}/esri");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await ReadDataAsync<CatalogEndpointDetail>(response);
        Assert.Equal("esri", detail.Endpoint.Key);
        Assert.True(detail.AutoMirror);
        Assert.Equal("2026-05-01T00:00:00Z", detail.LastRebuild);
        var item = Assert.Single(detail.Items);
        Assert.Equal("parcels", item.Id);
        Assert.Equal("Parcels FeatureServer", item.FromService);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/catalog-endpoints/{workspaceId}/{endpointKey}")]
    public async Task GetEndpoint_ForUnknownEndpoint_ReturnsNotFound()
    {
        var response = await _adminClient.GetAsync($"/api/v1/console/catalog-endpoints/{WorkspaceId}/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/catalog-endpoints/{workspaceId}/{endpointKey}/items/{itemId}")]
    public async Task GetItem_ReturnsFieldGroups()
    {
        var response = await _adminClient.GetAsync($"/api/v1/console/catalog-endpoints/{WorkspaceId}/esri/items/parcels");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = await ReadDataAsync<CatalogItem>(response);
        Assert.Equal("parcels", item.Id);
        Assert.True(item.AutoMirror);
        Assert.True(item.Live);
        var group = Assert.Single(item.Groups);
        Assert.Equal("Identity", group.Title);
        var field = Assert.Single(group.Fields);
        Assert.Equal(CatalogFieldStates.System, field.State);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/catalog-endpoints/{workspaceId}/{endpointKey}/items/{itemId}")]
    public async Task GetItem_ForUnknownItem_ReturnsNotFound()
    {
        var response = await _adminClient.GetAsync($"/api/v1/console/catalog-endpoints/{WorkspaceId}/esri/items/missing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/catalog-endpoints/{workspaceId}")]
    [Endpoint("GET /api/v1/console/catalog-endpoints/{workspaceId}/{endpointKey}")]
    [Endpoint("GET /api/v1/console/catalog-endpoints/{workspaceId}/{endpointKey}/items/{itemId}")]
    public async Task AnonymousRequests_AreRejectedWithoutAdminAuthorization()
    {
        var registryResponse = await _anonymousClient.GetAsync($"/api/v1/console/catalog-endpoints/{WorkspaceId}");
        Assert.Equal(HttpStatusCode.Unauthorized, registryResponse.StatusCode);

        var endpointResponse = await _anonymousClient.GetAsync($"/api/v1/console/catalog-endpoints/{WorkspaceId}/esri");
        Assert.Equal(HttpStatusCode.Unauthorized, endpointResponse.StatusCode);

        var itemResponse = await _anonymousClient.GetAsync($"/api/v1/console/catalog-endpoints/{WorkspaceId}/esri/items/parcels");
        Assert.Equal(HttpStatusCode.Unauthorized, itemResponse.StatusCode);
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

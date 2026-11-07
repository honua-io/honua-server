// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Console.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Honua.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Console;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.GetMetadata)]
public sealed class PublishedContentProjectionTests : IAsyncLifetime
{
    private const string AdminKey = "published-content-test-key";
    private readonly TestMetadataV2GraphProvider _provider = new(Graph());
    private readonly WebAppFixture _fixture;
    private HttpClient _admin = null!;

    public PublishedContentProjectionTests()
    {
        _fixture = new WebAppFixture()
            .ReplaceService<IMetadataV2GraphProvider>(_provider)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminKey);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _admin = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminKey));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    [Endpoint("GET /api/v1/console/content/{id}")]
    public async Task GetContent_GraphLifecycle_TracksPublishRepublishDisableAndDelete()
    {
        using var first = await ReadContentAsync("?type=layer");
        var layer = Assert.Single(first.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("publication", layer.GetProperty("id").GetString());
        Assert.Equal("Parcels", layer.GetProperty("title").GetString());
        Assert.EndsWith("/rest/services/Survey/Parcels/FeatureServer/0",
            layer.GetProperty("typeMetadata").GetProperty("url").GetString());

        var graph = Graph();
        _provider.SetGraph(graph with
        {
            Resources = [graph.Resources[0] with { Metadata = graph.Resources[0].Metadata with { Title = "Revised parcels" } }]
        });
        using var detailResponse = await _admin.GetAsync("/api/v1/console/content/publication");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        Assert.Equal("Revised parcels", detail.RootElement.GetProperty("data").GetProperty("title").GetString());

        _provider.SetGraph(graph with
        {
            Publications = [graph.Publications[0] with { Status = new() { Lifecycle = MetadataV2LifecycleStatus.Retired } }]
        });
        using var disabled = await ReadContentAsync();
        Assert.Empty(disabled.RootElement.GetProperty("items").EnumerateArray());
        using var absent = await _admin.GetAsync("/api/v1/console/content/publication");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);

        _provider.SetGraph(graph with { Services = [], Resources = [], Publications = [] });
        using var deleted = await ReadContentAsync();
        Assert.Equal(0, deleted.RootElement.GetProperty("total").GetInt32());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    [Endpoint("GET /api/v1/console/content/search")]
    public async Task GetContent_RestrictedResource_ExcludesLayerAndServiceFromTotalsAndSearch()
    {
        var graph = Graph();
        _provider.SetGraph(graph with
        {
            Resources = [graph.Resources[0] with { AccessPolicy = new() { AllowedRoles = ["survey-reader"] } }]
        });
        using var listed = await ReadContentAsync();
        Assert.Equal(0, listed.RootElement.GetProperty("total").GetInt32());
        using var searched = await ReadContentAsync("/search?q=Parcels");
        Assert.Equal(0, searched.RootElement.GetProperty("total").GetInt32());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    [Endpoint("DELETE /api/v1/console/content/{id}")]
    public async Task GetContent_PublishedIdentity_DoesNotCreateMutableContentRows()
    {
        using var listed = await ReadContentAsync("?itemType=service");
        Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
        using var removed = await _admin.DeleteAsync("/api/v1/console/content/publication");
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        using var stillPublished = await ReadContentAsync("?itemType=layer");
        var item = Assert.Single(stillPublished.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(["view"], item.GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
    }

    [IntegrationTheory]
    [InlineData("anonymous", 1)]
    [InlineData("member", 2)]
    [InlineData("reader", 3)]
    [Endpoint("GET /sharing/rest/search")]
    public async Task SearchPortal_CallerAccess_FiltersBeforeCounting(string caller, int expectedCount)
    {
        var graph = Graph();
        var policies = new AccessPolicy[]
        {
            new() { AllowAnonymous = true },
            new(),
            new() { AllowedRoles = [caller == "reader" ? "admin" : "survey-reader"] }
        };
        _provider.SetGraph(graph with
        {
            Services = policies.Select((policy, index) => graph.Services[0] with
            {
                Metadata = graph.Services[0].Metadata with { Id = "service-" + index, Name = "parcels-" + index },
                PublicName = "Parcels-" + index, AccessPolicy = policy
            }).ToArray(),
            Publications = policies.Select((_, index) => graph.Publications[0] with
            {
                Metadata = graph.Publications[0].Metadata with { Id = "publication-" + index },
                ServiceId = "service-" + index
            }).ToArray()
        });
        using var anonymous = _fixture.CreateClient();
        var client = caller == "anonymous" ? anonymous : _admin;
        using var response = await client.GetAsync("/sharing/rest/search?f=json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCount, payload.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(expectedCount, payload.RootElement.GetProperty("results").GetArrayLength());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    [Endpoint("GET /sharing/rest/search")]
    public async Task GetContent_UnadvertisedPublication_OmitsServiceAndLayerButKeepsNamedLookup()
    {
        var graph = Graph();
        var publicationJson = JsonSerializer.SerializeToNode(graph.Publications[0], MetadataV2JsonContext.Default.MetadataV2Publication)!;
        publicationJson["advertised"] = false;
        var hidden = publicationJson.Deserialize(MetadataV2JsonContext.Default.MetadataV2Publication)!;
        _provider.SetGraph(graph with { Publications = [hidden] });

        using var listed = await ReadContentAsync();
        Assert.Equal(0, listed.RootElement.GetProperty("total").GetInt32());
        using var response = await _admin.GetAsync("/sharing/rest/search?f=json");
        using var portal = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, portal.RootElement.GetProperty("total").GetInt32());
        var snapshot = await _provider.GetCurrentAsync();
        Assert.NotNull(snapshot.FindPublicationByLayerIndex("service", 0));
        Assert.True(snapshot.IsRoutable(hidden));
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    [Endpoint("GET /api/v1/console/content/{id}")]
    [Endpoint("PATCH /api/v1/console/content/{id}")]
    public async Task GetContent_StoredIdentityCollision_PreservesGraphAuthorizationAndOwnership()
    {
        var store = _fixture.Services.GetRequiredService<InMemoryConsoleContentStore>();
        await store.CreateAsync(new()
        {
            Id = "publication", Name = "collision", Title = "Stored collision",
            ItemType = ConsoleContentItemType.Layer, Visibility = ConsoleVisibility.Public
        });
        using var initial = await ReadContentAsync("?itemType=layer");
        Assert.Equal("Parcels", Assert.Single(initial.RootElement.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        var graph = Graph();
        _provider.SetGraph(graph with
        {
            Resources = [graph.Resources[0] with { AccessPolicy = new() { AllowedRoles = ["survey-reader"] } }]
        });
        using var denied = await ReadContentAsync();
        Assert.Equal(0, denied.RootElement.GetProperty("total").GetInt32());
        using var detail = await _admin.GetAsync("/api/v1/console/content/publication");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        using var patch = new StringContent("{\"title\":\"Changed\"}", System.Text.Encoding.UTF8, "application/json");
        using var patched = await _admin.PatchAsync("/api/v1/console/content/publication", patch);
        Assert.Equal(HttpStatusCode.NotFound, patched.StatusCode);
        Assert.Equal("Stored collision", (await store.GetAsync("publication"))!.Title);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    public async Task GetContent_MultipleAuthoredPages_PaginatesAfterCombiningWithGraphItems()
    {
        var store = _fixture.Services.GetRequiredService<InMemoryConsoleContentStore>();
        for (var index = 0; index < 201; index++)
        {
            await store.CreateAsync(new()
            {
                Id = $"authored-{index:D3}", Name = $"authored-{index:D3}",
                ItemType = ConsoleContentItemType.SavedMap
            });
        }
        using var first = await ReadContentAsync("?limit=200");
        Assert.Equal(203, first.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(200, first.RootElement.GetProperty("items").GetArrayLength());
        var cursor = first.RootElement.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));
        using var next = await ReadContentAsync("?limit=200&cursor=" + Uri.EscapeDataString(cursor!));
        Assert.Equal(203, next.RootElement.GetProperty("total").GetInt32());
        var remainder = next.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToArray();
        Assert.Equal(["authored-200", "publication", "service"], remainder);
    }

    private async Task<JsonDocument> ReadContentAsync(string suffix = "")
    {
        using var response = await _admin.GetAsync("/api/v1/console/content" + suffix);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(envelope.RootElement.GetProperty("data").GetRawText());
    }

    private static MetadataV2Graph Graph() => new()
    {
        Revision = 1,
        Folders = [new() { Metadata = new() { Id = "folder", Name = "Survey" } }],
        Services = [new()
        {
            Metadata = new() { Id = "service", Name = "internal", Title = "Parcels" },
            PublicName = "Parcels", FolderId = "folder", Protocols = [ServiceProtocols.FeatureServer],
            Status = new() { Lifecycle = MetadataV2LifecycleStatus.Active },
            AccessPolicy = new() { AllowAnonymous = true }
        }],
        Resources = [new()
        {
            Metadata = new() { Id = "resource", Name = "parcels", Title = "Parcels" },
            Status = new() { Lifecycle = MetadataV2LifecycleStatus.Active },
            AccessPolicy = new() { AllowAnonymous = true }
        }],
        Publications = [new()
        {
            Metadata = new() { Id = "publication", Name = "parcels" },
            ServiceId = "service", ResourceId = "resource", LayerIndex = 0,
            Status = new() { Lifecycle = MetadataV2LifecycleStatus.Active }
        }]
    };
}

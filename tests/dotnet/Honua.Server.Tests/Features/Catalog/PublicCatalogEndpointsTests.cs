// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Honua.Server.Tests.Features.Admin;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Catalog;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.GetMetadata)]
public sealed partial class PublicCatalogEndpointsTests : IAsyncLifetime
{
    private const string AdminKey = "catalog-test-key";
    private readonly TestMetadataV2GraphProvider _provider = new(Graph());
    private readonly WebAppFixture _fixture;
    private HttpClient _anonymous = null!;
    private HttpClient _member = null!;

    public PublicCatalogEndpointsTests()
    {
        _fixture = new WebAppFixture().ReplaceService<IMetadataV2GraphProvider>(_provider)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminKey);
                builder.UseSetting("Studio:EndUserAuthorization:Enabled", "true");
            }).ConfigureServices(services =>
            {
                services.RemoveAll<IPortalGroupStore>();
                services.RemoveAll<IPortalItemSharingStore>();
                services.RemoveAll<IPortalAudienceSnapshotProvider>();
                services.AddSingleton<IPortalGroupStore, InMemoryPortalGroupStore>();
                services.AddSingleton<IPortalItemSharingStore, InMemoryPortalItemSharingStore>();
                services.RemoveAll<Honua.Core.Features.Studio.Abstractions.IStudioPackageStore>();
                services.AddSingleton<Honua.Core.Features.Studio.Abstractions.IStudioPackageStore,
                    Honua.Core.Features.Studio.Services.InMemoryStudioPackageStore>();
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, RlsClaimsTestAuthHandler>(
                    RlsClaimsTestAuthHandler.SchemeName, _ => { });
                services.PostConfigureAll<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = RlsClaimsTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = RlsClaimsTestAuthHandler.SchemeName;
                    options.DefaultScheme = RlsClaimsTestAuthHandler.SchemeName;
                });
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _anonymous = _fixture.CreateClient();
        _member = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, "catalog-reader"));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("anonymous", 1)]
    [InlineData("signed-in", 2)]
    [InlineData("allowed-role", 3)]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_CallerAccess_FiltersItemsFoldersAndFacetsBeforePaging(string caller, int visibleServices)
    {
        var graph = Graph();
        var policies = new AccessPolicy[]
        {
            new() { AllowAnonymous = true }, new(),
            new() { AllowedRoles = ["survey-reader"] }
        };
        _provider.SetGraph(graph with
        {
            Folders = policies.Select((_, i) => graph.Folders[0] with
            { Metadata = graph.Folders[0].Metadata with { Id = $"folder-{i}", Name = $"Folder-{i}" } }).ToArray(),
            Services = policies.Select((policy, i) => graph.Services[0] with
            {
                Metadata = graph.Services[0].Metadata with { Id = $"service-{i}", Name = $"service-{i}", Publisher = $"owner-{i}", Tags = [$"tag-{i}"] },
                FolderId = $"folder-{i}", AccessPolicy = policy
            }).ToArray(),
            Publications = policies.Select((_, i) => graph.Publications[0] with
            { Metadata = graph.Publications[0].Metadata with { Id = $"publication-{i}" }, ServiceId = $"service-{i}" }).ToArray()
        });
        if (caller == "allowed-role")
        {
            _member.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.RolesHeader, "survey-reader");
        }
        using var result = await ReadAsync(caller == "anonymous" ? _anonymous : _member, "?limit=1");
        Assert.Equal(visibleServices * 3, result.RootElement.GetProperty("total").GetInt32());
        Assert.Single(result.RootElement.GetProperty("items").EnumerateArray());
        var facets = result.RootElement.GetProperty("facets");
        Assert.Equal(visibleServices, facets.GetProperty("folder").GetArrayLength());
        Assert.Equal(visibleServices + 1, facets.GetProperty("tag").GetArrayLength());
        Assert.All(facets.GetProperty("type").EnumerateArray(), bucket =>
            Assert.Equal(visibleServices, bucket.GetProperty("count").GetInt32()));
        for (var i = visibleServices; i < 3; i++)
        {
            Assert.DoesNotContain($"owner-{i}", result.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain($"Folder-{i}", result.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain($"tag-{i}", result.RootElement.GetRawText(), StringComparison.Ordinal);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    [Endpoint("GET /api/v1/catalog/{id}")]
    public async Task GetCatalog_DeniedResource_OmitsServiceFolderFacetsAndDetail()
    {
        var graph = Graph();
        _provider.SetGraph(graph with { Resources = [graph.Resources[0] with
        { AccessPolicy = new() { AllowedRoles = ["survey-reader"] } }] });
        using var result = await ReadAsync(_member);
        Assert.Equal(0, result.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(result.RootElement.GetProperty("items").EnumerateArray());
        Assert.All(result.RootElement.GetProperty("facets").EnumerateObject(), facet => Assert.Empty(facet.Value.EnumerateArray()));
        foreach (var id in new[] { "service", "publication", "folder" })
        {
            using var response = await _member.GetAsync("/api/v1/catalog/" + id);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_Filters_IntersectBeforeFacetCounts()
    {
        using var result = await ReadAsync(_anonymous, "?q=parcel&type=layer&folder=Survey&owner=surveyor&tag=land&bbox=-158,20,-155,23&datetime=2026-01-01T00:00:00Z/2026-12-31T00:00:00Z");
        var item = Assert.Single(result.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("publication", item.GetProperty("id").GetString());
        Assert.Equal("public", item.GetProperty("access").GetString());
        Assert.Equal(1, result.RootElement.GetProperty("facets").GetProperty("type")[0].GetProperty("count").GetInt32());
        using var outside = await ReadAsync(_anonymous, "?bbox=0,0,1,1");
        Assert.Equal(0, outside.RootElement.GetProperty("total").GetInt32());
    }

    [IntegrationTheory]
    [InlineData("?limit=0")]
    [InlineData("?offset=-1")]
    [InlineData("?bbox=NaN,0,1,1")]
    [InlineData("?bbox=0,2,1,1")]
    [InlineData("?datetime=invalid")]
    [InlineData("?datetime=2027-01-01T00:00:00Z/2026-01-01T00:00:00Z")]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_InvalidFilter_ReturnsBadRequest(string query)
    {
        using var response = await _anonymous.GetAsync("/api/v1/catalog" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    [Endpoint("GET /api/v1/catalog/{id}")]
    public async Task GetCatalog_PublicationHiddenInJson_OmitsCatalogListAndDetail()
    {
        var graph = Graph();
        var publicationJson = JsonSerializer.Serialize(graph.Publications[0], MetadataV2JsonContext.Default.MetadataV2Publication);
        using var document = JsonDocument.Parse(publicationJson);
        var members = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        members["advertised"] = JsonSerializer.SerializeToElement(false);
        var publication = JsonSerializer.Deserialize<MetadataV2Publication>(JsonSerializer.Serialize(members), MetadataV2JsonContext.Default.MetadataV2Publication)!;
        graph = graph with { Publications = [publication] };
        _provider.SetGraph(graph);
        using var result = await ReadAsync(_anonymous);
        Assert.Equal(0, result.RootElement.GetProperty("total").GetInt32());
        using var detail = await _anonymous.GetAsync("/api/v1/catalog/publication");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetCatalog_ProtocolLinks_UsesPublicManifestAndEnabledServiceProtocols()
    {
        var graph = Graph();
        _provider.SetGraph(graph with { Services = [graph.Services[0] with { Protocols = ServiceProtocols.All }] });
        using var manifestResponse = await _anonymous.GetAsync("/api/v1/capabilities/manifest");
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync());
        var protocols = manifest.RootElement.GetProperty("protocolEndpoints").EnumerateArray()
            .Select(endpoint => endpoint.GetProperty("protocol").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(protocols);
        using var result = await ReadAsync(_anonymous, "?type=service");
        var service = Assert.Single(result.RootElement.GetProperty("items").EnumerateArray());
        var links = service.GetProperty("endpoints").EnumerateArray().ToArray();
        Assert.NotEmpty(links);
        var expected = manifest.RootElement.GetProperty("protocolEndpoints").EnumerateArray()
            .Where(endpoint => !endpoint.GetProperty("preferredPublicationRequired").GetBoolean())
            .Where(endpoint => endpoint.GetProperty("protocol").GetString() is not (ServiceProtocols.OgcFeatures or ServiceProtocols.OgcApiMaps or ServiceProtocols.OgcApiTiles))
            .Select(endpoint => endpoint.GetProperty("protocol").GetString()).ToArray();
        Assert.All(expected, protocol => Assert.Contains(links, link => link.GetProperty("protocol").GetString() == protocol));
        Assert.All(links, link => Assert.Contains(link.GetProperty("protocol").GetString(), protocols));
        var restLink = Assert.Single(links, link => link.GetProperty("protocol").GetString() == ServiceProtocols.FeatureServer);
        Assert.EndsWith("/rest/services/Survey/Parcels/FeatureServer", restLink.GetProperty("href").GetString());
        _provider.SetGraph(graph);
        using var narrowed = await ReadAsync(_anonymous, "?type=service");
        var item = Assert.Single(narrowed.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(ServiceProtocols.FeatureServer, Assert.Single(item.GetProperty("endpoints").EnumerateArray()).GetProperty("protocol").GetString());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_UnusedFolderWithSameName_DoesNotExposeFolderMetadata()
    {
        var graph = Graph();
        _provider.SetGraph(graph with { Folders = [graph.Folders[0], graph.Folders[0] with
        { Metadata = graph.Folders[0].Metadata with { Id = "other-folder", Tenant = "other-tenant", Title = "Restricted folder" } }] });
        using var result = await ReadAsync(_anonymous);
        Assert.Equal(3, result.RootElement.GetProperty("total").GetInt32());
        Assert.DoesNotContain("Restricted folder", result.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [UnitTest]
    public void Publication_AdvertisingOmittedFromJson_DefaultsToAdvertised()
    {
        var publication = JsonSerializer.Deserialize("{}", MetadataV2JsonContext.Default.MetadataV2Publication)!;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(publication, MetadataV2JsonContext.Default.MetadataV2Publication));
        Assert.True(document.RootElement.GetProperty("advertised").GetBoolean());
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync("/api/v1/catalog" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static MetadataV2Graph Graph() => new()
    {
        Revision = 1,
        Folders = [new() { Metadata = new() { Id = "folder", Name = "Survey" } }],
        Services = [new()
        {
            Metadata = new() { Id = "service", Name = "parcels", Title = "Parcels", Publisher = "surveyor", Tags = ["land"] },
            FolderId = "folder", PublicName = "Parcels", Protocols = [ServiceProtocols.FeatureServer],
            AccessPolicy = new() { AllowAnonymous = true }
        }],
        Resources = [new()
        {
            Metadata = new() { Id = "resource", Name = "parcels", Title = "Parcel boundaries", Publisher = "surveyor", Tags = ["land"] },
            AccessPolicy = new() { AllowAnonymous = true },
            Spatial = new() { GeometryType = MetadataV2GeometryType.Polygon, SpatialReference = MetadataV2SpatialReference.Wgs84, Bbox = new() { West = -158, South = 20, East = -155, North = 23 } },
            Temporal = new() { Extent = new() { Start = DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), End = DateTimeOffset.Parse("2026-06-30T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture) } }
        }],
        Publications = [new()
        {
            Metadata = new() { Id = "publication", Name = "parcels" },
            ServiceId = "service", ResourceId = "resource", LayerIndex = 0, IsPrimary = true
        }]
    };
}

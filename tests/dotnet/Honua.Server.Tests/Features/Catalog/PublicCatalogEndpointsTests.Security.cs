// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Catalog;

public sealed partial class PublicCatalogEndpointsTests
{
    [IntegrationTheory]
    [InlineData("restricted")]
    [InlineData("hidden")]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_MixedPublications_ExcludesUndiscoverableDetailsAndExtents(string omitted)
    {
        var graph = Graph();
        _provider.SetGraph(graph with
        {
            Resources = [graph.Resources[0], graph.Resources[0] with
            {
                Metadata = graph.Resources[0].Metadata with { Id = "sensitive-resource", Name = "sensitive", Title = "Sensitive title", Tags = ["sensitive-tag"] },
                AccessPolicy = omitted == "restricted" ? new() { AllowedRoles = ["survey-reader"] } : new() { AllowAnonymous = true },
                Spatial = graph.Resources[0].Spatial! with { Bbox = new() { West = 0, South = 0, East = 1, North = 1 } }
            }],
            Publications = [graph.Publications[0], graph.Publications[0] with
            {
                Metadata = graph.Publications[0].Metadata with { Id = "sensitive-publication", Name = "sensitive" },
                ResourceId = "sensitive-resource", Advertised = omitted != "hidden", LayerIndex = 1
            }]
        });
        using var result = await ReadAsync(_anonymous);
        Assert.Equal(3, result.RootElement.GetProperty("total").GetInt32());
        Assert.DoesNotContain("sensitive", result.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var service = Assert.Single(result.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("type").GetString() == "service");
        Assert.Equal("publication", Assert.Single(service.GetProperty("layers").EnumerateArray()).GetString());
        Assert.Equal(-155, service.GetProperty("extent").GetProperty("east").GetDouble());
        using var filtered = await ReadAsync(_anonymous, "?bbox=0,0,1,1");
        Assert.Equal(0, filtered.RootElement.GetProperty("total").GetInt32());
        Assert.All(filtered.RootElement.GetProperty("facets").EnumerateObject(), facet => Assert.Empty(facet.Value.EnumerateArray()));
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_EmptyOptionalFilters_MatchesUnfilteredListing()
    {
        using var result = await ReadAsync(_anonymous, "?q=&type=&protocol=&folder=&tag=&owner=&datetime=");
        Assert.Equal(3, result.RootElement.GetProperty("total").GetInt32());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    [Endpoint("GET /api/v1/catalog/{id}")]
    public async Task GetCatalog_CrossTypeIdentityCollision_RejectsAmbiguousDetail()
    {
        var graph = Graph();
        _provider.SetGraph(graph with
        {
            Folders = [graph.Folders[0] with { Metadata = graph.Folders[0].Metadata with { Id = "service" } }],
            Services = [graph.Services[0] with { FolderId = "service" }]
        });
        using var response = await _anonymous.GetAsync("/api/v1/catalog/service");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_AntimeridianBounds_SelectsIntersectingItems()
    {
        using var result = await ReadAsync(_anonymous, "?bbox=170,19,-150,24");
        Assert.Equal(3, result.RootElement.GetProperty("total").GetInt32());
        using var outside = await ReadAsync(_anonymous, "?bbox=170,19,-170,24");
        Assert.Equal(0, outside.RootElement.GetProperty("total").GetInt32());
    }
    [IntegrationTheory]
    [InlineData("service")]
    [InlineData("resource")]
    [InlineData("publication")]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_ForeignTenantMetadata_ExcludesEveryFacet(string scopedEntity)
    {
        var graph = Graph();
        _provider.SetGraph(graph with
        {
            Services = [graph.Services[0] with { Metadata = graph.Services[0].Metadata with { Tenant = scopedEntity == "service" ? "other-tenant" : null } }],
            Resources = [graph.Resources[0] with { Metadata = graph.Resources[0].Metadata with { Tenant = scopedEntity == "resource" ? "other-tenant" : null } }],
            Publications = [graph.Publications[0] with { Metadata = graph.Publications[0].Metadata with { Tenant = scopedEntity == "publication" ? "other-tenant" : null } }]
        });
        using var result = await ReadAsync(_anonymous);
        Assert.Equal(0, result.RootElement.GetProperty("total").GetInt32());
        Assert.All(result.RootElement.GetProperty("facets").EnumerateObject(), facet => Assert.Empty(facet.Value.EnumerateArray()));
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_GroupAudience_TracksMembershipWithoutGrantingAnonymousAccess()
    {
        var graph = Graph();
        _provider.SetGraph(graph with { Services = [graph.Services[0] with { AccessPolicy = new() { AllowedRoles = ["publisher"] } }] });
        var groups = _fixture.Services.GetRequiredService<IPortalGroupStore>();
        var sharing = _fixture.Services.GetRequiredService<IPortalItemSharingStore>();
        var group = await groups.CreateAsync(new("Surveyors", null, PortalGroupAccess.Private, []), "publisher", CancellationToken.None);
        await groups.AddUsersAsync(group.Id, ["catalog-reader"], CancellationToken.None);
        await sharing.ShareAsync("service", "publisher", new(false, false, [group.Id]), CancellationToken.None);
        using var member = await ReadAsync(_member);
        Assert.Equal(3, member.RootElement.GetProperty("total").GetInt32());
        using var anonymous = await ReadAsync(_anonymous);
        Assert.Equal(0, anonymous.RootElement.GetProperty("total").GetInt32());
        await groups.RemoveUsersAsync(group.Id, ["catalog-reader"], CancellationToken.None);
        using var removed = await ReadAsync(_member);
        Assert.Equal(0, removed.RootElement.GetProperty("total").GetInt32());
        Assert.All(removed.RootElement.GetProperty("facets").EnumerateObject(), facet => Assert.Empty(facet.Value.EnumerateArray()));
    }

}

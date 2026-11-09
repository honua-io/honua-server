// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Catalog;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.GetMetadata)]
public sealed partial class CatalogAudienceEndpointsTests : IAsyncLifetime
{
    private readonly TestMetadataV2GraphProvider _provider = new(Graph());
    private readonly WebAppFixture _fixture;
    private HttpClient _anonymous = null!;
    private HttpClient _member = null!;

    public CatalogAudienceEndpointsTests()
    {
        _fixture = new WebAppFixture().ReplaceService<IMetadataV2GraphProvider>(_provider)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "catalog-audience-test");
            }).ConfigureServices(services =>
            {
                services.RemoveAll<IPortalGroupStore>();
                services.RemoveAll<IPortalItemSharingStore>();
                services.RemoveAll<IPortalAudienceSnapshotProvider>();
                services.AddSingleton<IPortalGroupStore, InMemoryPortalGroupStore>();
                services.AddSingleton<IPortalItemSharingStore, InMemoryPortalItemSharingStore>();
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
        _member = _fixture.CreateClient(client =>
            client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, "catalog-reader"));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("public", false, 1)]
    [InlineData("public", true, 1)]
    [InlineData("staff", true, 2)]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_CatalogAudience_ConstrainsItemsAndFacets(string catalog, bool signedIn, int expected)
    {
        using var response = await (signedIn ? _member : _anonymous).GetAsync($"/api/v1/catalogs/{catalog}/items?kind=service&limit=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var page = document.RootElement;
        Assert.Equal(expected, page.GetProperty("total").GetInt32());
        Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(expected, page.GetProperty("facets").GetProperty("owner").GetArrayLength());
        Assert.DoesNotContain("secret-owner", page.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-tag", page.GetRawText(), StringComparison.Ordinal);
        if (catalog == "public")
            Assert.DoesNotContain("staff-owner", page.GetRawText(), StringComparison.Ordinal);
    }

    [IntegrationTheory]
    [InlineData("staff")]
    [InlineData("responders")]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    [Endpoint("GET /api/v1/catalogs/{catalogId}")]
    public async Task GetCatalog_AnonymousOutsideAudience_ConcealsCatalogAndItems(string catalog)
    {
        foreach (var suffix in new[] { "", "/items" })
        {
            using var response = await _anonymous.GetAsync($"/api/v1/catalogs/{catalog}{suffix}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("secret-owner", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_RoleAudience_IntersectsVisitorAccessAndCurrentGrants()
    {
        _member.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.RolesHeader, "responder");
        using var first = await _member.GetAsync("/api/v1/catalogs/responders/items?kind=service");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var document = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal(3, document.RootElement.GetProperty("total").GetInt32());
        _member.DefaultRequestHeaders.Remove(RlsClaimsTestAuthHandler.RolesHeader);
        using var revoked = await _member.GetAsync("/api/v1/catalogs/responders/items?kind=service");
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs")]
    public async Task ListCatalogs_Anonymous_ListsOnlyAccessibleConfiguredCatalogs()
    {
        using var response = await _anonymous.GetAsync("/api/v1/catalogs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var catalog = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("public", catalog.GetProperty("id").GetString());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items/{kind}")]
    public async Task GetItem_RestrictedOrAbsent_UsesSameNotFoundResponse()
    {
        using var denied = await _member.GetAsync("/api/v1/catalogs/public/items/service?id=secret");
        using var absent = await _member.GetAsync("/api/v1/catalogs/public/items/service?id=missing");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var document = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.DoesNotContain("secret-owner", document.RootElement.GetRawText(), StringComparison.Ordinal);
    }
}

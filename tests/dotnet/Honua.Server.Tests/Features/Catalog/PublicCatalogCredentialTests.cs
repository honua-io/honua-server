// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Catalog;

[Collection("Database")]
public sealed class PublicCatalogCredentialTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    [Endpoint("GET /api/v1/catalog/{id}")]
    public async Task GetCatalog_ScopedCredential_ExcludesPublicServicesOutsideItsGrant()
    {
        var keys = new InMemoryAdminApiKeyStore();
        await using var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            }).ConfigureServices(services =>
            {
                services.RemoveAll<IAdminApiKeyStore>();
                services.AddSingleton<IAdminApiKeyStore>(keys);
            });
        await fixture.InitializeAsync();
        var graph = fixture.GetCurrentV2GraphSnapshot().Graph;
        var service = Assert.Single(graph.Services, service => service.Metadata.Name == WebAppFixture.TestServiceId);
        var publications = graph.Publications.Where(publication => publication.ServiceId == service.Metadata.Id).ToArray();
        var provider = (TestMetadataV2GraphProvider)fixture.Services.GetRequiredService<IMetadataV2GraphProvider>();
        provider.SetGraph(graph with
        {
            Services = [service, service with
            {
                Metadata = service.Metadata with { Id = "out-of-scope-service", Name = "unscoped", Title = "Out of scope", Tags = ["out-of-scope"] },
                PublicName = "Unscoped"
            }],
            Publications = publications.Concat(publications.Select(publication => publication with
            {
                Metadata = publication.Metadata with { Id = "out-of-scope-" + publication.Metadata.Id },
                ServiceId = "out-of-scope-service"
            })).ToArray()
        }, schema: fixture.CurrentSchema);
        var issued = await keys.CreateAsync("catalog-reader", ["read:" + WebAppFixture.TestServiceId], null, "test", CancellationToken.None);
        using var client = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", issued.Key));
        using var response = await client.GetAsync("/api/v1/catalog?type=service&limit=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, document.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(service.Metadata.Id, Assert.Single(document.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        Assert.DoesNotContain("out-of-scope", document.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        using var denied = await client.GetAsync("/api/v1/catalog/out-of-scope-service");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalog")]
    public async Task GetCatalog_ConfiguredDeploymentProfile_ServesOnlyEnabledProtocolLinks()
    {
        await using var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseSetting("DeploymentProfile:SchemaVersion", "1.0.0");
            builder.UseSetting("DeploymentProfile:EnabledCapabilities", "discovery.public-catalog,serve.geoservices-featureserver");
        });
        await fixture.InitializeAsync();
        using var response = await fixture.Client.GetAsync("/api/v1/catalog?type=service");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.NotEmpty(items);
        var links = items.SelectMany(item => item.GetProperty("endpoints").EnumerateArray()).ToArray();
        Assert.NotEmpty(links);
        Assert.All(links, link => Assert.Equal("FeatureServer", link.GetProperty("protocol").GetString()));
    }
}

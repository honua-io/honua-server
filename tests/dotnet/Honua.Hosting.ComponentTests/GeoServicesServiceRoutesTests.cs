// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Caching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Honua.Hosting.ComponentTests;

public sealed class GeoServicesServiceRoutesTests
{
    [Theory]
    [InlineData("/rest/services", "FeatureServer")]
    [InlineData("/rest/services", "MapServer")]
    [InlineData("/rest/services", "ImageServer")]
    [InlineData("/rest/services", "VectorTileServer")]
    [InlineData("/rest/services", "GPServer")]
    [InlineData("/rest/services", "NAServer")]
    [InlineData("/rest/services", "VersionManagementServer")]
    [InlineData("/services", "ImageServer")]
    [InlineData("/services", "GPServer")]
    [InlineData("/rest/services", "ImageServer", "{serviceId:regex(^(?!\\d+$).+$)}")]
    public async Task FolderRoute_NamedServicePrefix_PreservesBindingAndBody(
        string root, string serviceType, string serviceParameter = "{serviceId}")
    {
        await using var app = CreateApp();
        app.UseGeoServicesServicePaths();
        app.MapMethods($"{root}/{serviceParameter}/{serviceType}/{{layerId:int}}/query", ["GET", "POST"],
            async (string serviceId, int layerId, HttpRequest request) =>
            {
                using var reader = new StreamReader(request.Body);
                return Results.Text($"{serviceId}:{layerId}:{await reader.ReadToEndAsync()}");
            }).WithName("ServiceQuery");
        app.MapGeoServicesServicePaths();
        await app.StartAsync();
        using var client = app.GetTestClient();
        foreach (var path in new[] { "Public/Parcels", "Previous/Parcels" })
        {
            using var body = new StringContent("unchanged=body", Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await client.PostAsync($"{root}/{path}/{serviceType}/7/query", body);
            (await response.Content.ReadAsStringAsync()).Should().Be("internal-parcels:7:unchanged=body");
        }
        using var invalidConstraint = await client.GetAsync($"{root}/Public/Parcels/{serviceType}/not-a-number/query");
        invalidConstraint.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();
        endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .Should().BeEquivalentTo("ServiceQuery", "ServiceQueryFolder");
    }

    [Theory]
    [InlineData("Unknown/Parcels")]
    [InlineData("Public/Other")]
    [InlineData("internal-parcels")]
    [InlineData("Retired/Parcels")]
    public async Task FolderRoute_UnreservedOrRetiredPath_DoesNotInvokeHandler(string servicePath)
    {
        await using var app = CreateApp();
        app.MapGet("/rest/services/{serviceId}/FeatureServer", () => Results.Text("must not run"));
        app.MapGeoServicesServicePaths();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/rest/services/{servicePath}/FeatureServer");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
    }

    [Fact]
    public async Task FolderRoute_NumericAndLiteralPrefixes_KeepExistingRoutes()
    {
        await using var app = CreateApp();
        app.MapGet("/rest/services/{id:int}/ImageServer", (int id) => Results.Text($"raster:{id}"));
        app.MapGet("/rest/services/Utilities/PrintingTools/GPServer", () => Results.Text("printing"));
        app.MapGeoServicesServicePaths();
        await app.StartAsync();
        using var client = app.GetTestClient();
        (await client.GetStringAsync("/rest/services/123/ImageServer")).Should().Be("raster:123");
        (await client.GetStringAsync("/rest/services/Utilities/PrintingTools/GPServer")).Should().Be("printing");
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("alpha", true)]
    [InlineData("beta", false)]
    [InlineData(null, false)]
    public async Task FolderRoute_TenantScopedReservation_RequiresMatchingTenant(string? tenant, bool resolves)
    {
        await using var app = CreateApp("alpha", tenant);
        app.MapGet("/rest/services/{serviceId}/FeatureServer", () => Results.Text("authorized scope"));
        app.MapGeoServicesServicePaths();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/rest/services/Previous/Parcels/FeatureServer");
        var body = await response.Content.ReadAsStringAsync();
        if (resolves)
        {
            body.Should().Be("authorized scope");
        }
        else
        {
            using var document = JsonDocument.Parse(body);
            document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
        }
    }

    [Theory]
    [InlineData("/rest/services/Public/Parcels/MapServer/WMS", "WMS", null, "http://localhost/mounted/rest/services/Public/Parcels/MapServer/WMS")]
    [InlineData("/rest/services/Public/Parcels/MapServer/WMTS/1.0.0/WMTSCapabilities.xml", "WMTS", null, "http://localhost/mounted/rest/services/Public/Parcels/MapServer/WMTS")]
    [InlineData("/ogc/services/internal-parcels/wmts", "WMTS", "https://public.example/external", "https://public.example/external/ogc/services/internal-parcels/wmts")]
    [InlineData("/rest/services/imagery#west/ImageServer/WMTS", "WMTS", null, "http://localhost/mounted/rest/services/imagery%23west/ImageServer/WMTS")]
    public void ProtocolUrl_IncomingBase_EncodesSegmentsOnce(string path, string protocol, string? publicBase, string expected)
    {
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Public:BaseUrl"] = publicBase
            }).Build()).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.PathBase = "/mounted";
        context.Request.Path = path;
        GeoServicesServiceUrls.RequestedProtocol(context, protocol).Should().Be(expected);
    }

    [Fact]
    public async Task CatalogCache_NewRevision_DoesNotReusePriorDirectoryResponse()
    {
        await using var app = CreateApp();
        app.UseOutputCache();
        var reads = 0;
        app.MapGet("/rest/services", () => Results.Text($"read-{++reads}"))
            .WithMetadata(new GeoServicesServiceRoutes.CatalogRoute())
            .CacheOutput(policy => policy.AddPolicy<RouteTagOutputCachePolicy>());
        await app.StartAsync();
        using var client = app.GetTestClient();
        (await client.GetStringAsync("/rest/services")).Should().Be("read-1");
        (await client.GetStringAsync("/rest/services")).Should().Be("read-1");
        var provider = app.Services.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await provider.GetCurrentAsync();
        provider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new MetadataV2GraphSnapshot(snapshot.Graph, "revision-two", DateTimeOffset.UnixEpoch));
        (await client.GetStringAsync("/rest/services")).Should().Be("read-2");
    }

    private static WebApplication CreateApp(string? serviceTenant = null, string? requestTenant = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOutputCache();
        var graph = new MetadataV2Graph
        {
            Folders = [new() { Metadata = new() { Id = "folder", Name = "Public" } }],
            Services = [new() { Metadata = new() { Id = "facet", Name = "internal-parcels" }, PublicName = "Parcels", FolderId = "folder" }],
            ServicePathAliases =
            [
                new() { FolderName = "Previous", PublicName = "Parcels", TargetFacetIds = ["facet"] },
                new() { FolderName = "Retired", PublicName = "Parcels", TargetFacetIds = ["facet"], Retired = true }
            ]
        };
        var provider = Substitute.For<IMetadataV2GraphProvider>();
        graph = graph with
        {
            Services = graph.Services.Select(service => service with { Metadata = service.Metadata with { Tenant = serviceTenant } }).ToArray(),
            ServicePathAliases = graph.ServicePathAliases.Select(alias => alias with { Tenant = serviceTenant }).ToArray()
        };
        provider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new MetadataV2GraphSnapshot(graph, "revision-one", DateTimeOffset.UnixEpoch));
        builder.Services.AddSingleton(provider);
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns(requestTenant);
        builder.Services.AddSingleton(tenantContext);
        return builder.Build();
    }
}

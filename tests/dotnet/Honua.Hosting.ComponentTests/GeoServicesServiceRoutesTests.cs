// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task FolderRoute_NamedServicePrefix_PreservesBindingAndBody(string root, string serviceType)
    {
        await using var app = CreateApp();
        app.UseGeoServicesServicePaths();
        app.MapMethods($"{root}/{{serviceId}}/{serviceType}/{{layerId:int}}/query", ["GET", "POST"],
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

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
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
        provider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new MetadataV2GraphSnapshot(graph, "revision-one", DateTimeOffset.UnixEpoch));
        builder.Services.AddSingleton(provider);
        return builder.Build();
    }
}

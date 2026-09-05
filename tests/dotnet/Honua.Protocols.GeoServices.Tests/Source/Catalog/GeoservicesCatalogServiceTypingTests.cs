// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

/// <summary>
/// Regression tests for honua-server#1853 — the Esri REST Services Directory
/// (<c>GET /rest/services</c>) must type each service from the Esri protocols it
/// actually exposes: vector feature services as <c>FeatureServer</c>, raster/coverage
/// services as <c>ImageServer</c> (or <c>MapServer</c> for rendered-map services),
/// geoprocessing services as <c>GPServer</c>, and
/// a service reachable under multiple Esri types once per type — instead of flattening
/// everything to <c>FeatureServer</c>.
/// </summary>
[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GeoservicesCatalog)]
public sealed class GeoservicesCatalogServiceTypingTests
{
    private const string VectorServiceName = "typing-vector";
    private const string RasterServiceName = "typing-raster";
    private const string MultiTypeServiceName = "typing-multi";
    private const string GpServiceName = "typing-gp";
    private const string DuplicateServiceName = "typing-duplicate";

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_RasterService_TypedAsImageServerNotFeatureServer()
    {
        var rasterStore = BuildRasterStoreWithRasters();

        await using var fixture = await CreateFixtureAsync(rasterStore);

        var services = await GetServicesByNameAsync(fixture, RasterServiceName);

        // The raster service exposes only the ImageServer protocol, so it must NOT
        // appear as a FeatureServer in the catalog (the #1853 mis-typing).
        services.Should().NotBeEmpty();
        services.Should().Contain(type => type == "ImageServer");
        services.Should().NotContain(type => type == "FeatureServer");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_VectorService_TypedAsFeatureServer()
    {
        var rasterStore = BuildRasterStoreWithRasters();

        await using var fixture = await CreateFixtureAsync(rasterStore);

        var services = await GetServicesByNameAsync(fixture, VectorServiceName);

        services.Should().ContainSingle();
        services.Should().Contain(type => type == "FeatureServer");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_MultiProtocolService_ListedOncePerEsriType()
    {
        var rasterStore = BuildRasterStoreWithRasters();

        await using var fixture = await CreateFixtureAsync(rasterStore);

        var services = await GetServicesByNameAsync(fixture, MultiTypeServiceName);

        // A service reachable as both FeatureServer and MapServer is listed once per
        // type (Esri Services Directory semantics), not collapsed to its primary
        // protocol only.
        services.Should().Contain(type => type == "FeatureServer");
        services.Should().Contain(type => type == "MapServer");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_LayerlessGpService_TypedAsGpServer()
    {
        var rasterStore = BuildRasterStoreWithRasters();

        await using var fixture = await CreateFixtureAsync(rasterStore);

        var services = await GetServicesByNameAsync(fixture, GpServiceName);

        services.Should().ContainSingle().Which.Should().Be("GPServer");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_LayerlessGpServiceRequiresServingLifecycle()
    {
        foreach (var lifecycle in new[]
                 {
                     MetadataV2LifecycleStatus.Draft,
                     MetadataV2LifecycleStatus.Archived,
                     MetadataV2LifecycleStatus.Retired,
                 })
        {
            var rasterStore = BuildRasterStoreWithRasters();
            var graph = BuildTypingGraph();
            graph = graph with
            {
                Services = graph.Services
                    .Select(service => service.Metadata.Id == "svc-typing-gp"
                        ? service with { Status = new MetadataV2Status { Lifecycle = lifecycle } }
                        : service)
                    .ToArray(),
            };

            await using var fixture = await CreateFixtureAsync(rasterStore, graph);

            var services = await GetServicesByNameAsync(fixture, GpServiceName);

            services.Should().BeEmpty($"{lifecycle} services must not be advertised");
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_DuplicatePublicationNameAndType_ReturnsOneEntry()
    {
        var builder = new TestMetadataV2GraphBuilder();
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        foreach (var suffix in new[] { "a", "b" })
        {
            builder
                .AddResource($"res-typing-duplicate-{suffix}", "Duplicate Layer", MetadataV2ResourceType.FeatureDataset,
                    spatial: PointSpatial(), accessPolicy: anonymous)
                .AddService($"svc-typing-duplicate-{suffix}", DuplicateServiceName,
                    protocols: [ServiceProtocols.FeatureServer], accessPolicy: anonymous)
                .AddPublication($"pub-typing-duplicate-{suffix}", $"svc-typing-duplicate-{suffix}",
                    $"res-typing-duplicate-{suffix}", layerIndex: 710, serviceLocalId: "710",
                    publicationType: MetadataV2PublicationType.EsriFeatureLayer);
        }

        await using var fixture = await CreateFixtureAsync(
            BuildRasterStoreWithRasters(),
            builder.Build());

        var services = await GetServicesByNameAsync(fixture, DuplicateServiceName);

        services.Should().ContainSingle().Which.Should().Be("FeatureServer");
    }

    private static async Task<string[]> GetServicesByNameAsync(WebAppFixture fixture, string serviceName)
    {
        var response = await fixture.Client.GetAsync("/rest/services?f=json");
        response.Be200Ok();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement
            .GetProperty("services")
            .EnumerateArray()
            .Where(service =>
                service.TryGetProperty("name", out var name) &&
                string.Equals(name.GetString(), serviceName, StringComparison.Ordinal))
            .Select(service => service.GetProperty("type").GetString()!)
            .ToArray();
    }

    private static IRasterStore BuildRasterStoreWithRasters()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        static RasterInfo Raster(int layerId) => new()
        {
            Id = 1,
            LayerId = layerId,
            Name = "typing-raster-tile",
            Width = 256,
            Height = 256,
            BandCount = 3,
            PixelType = "8BUI",
            Srid = 4326,
            CreatedAt = DateTimeOffset.UtcNow
        };
        rasterStore.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new[] { Raster(callInfo.ArgAt<int>(0)) });
        rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult<RasterInfo?>(Raster(callInfo.ArgAt<int>(0))));
        return rasterStore;
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(
        IRasterStore rasterStore,
        MetadataV2Graph? graph = null)
    {
        var provider = new TestMetadataV2GraphProvider(graph ?? BuildTypingGraph());

        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton(provider);
                services.AddSingleton<IMetadataV2GraphProvider>(provider);
                services.AddSingleton<IMetadataV2GraphStore>(provider);
                services.AddSingleton(rasterStore);
            });

        await fixture.InitializeAsync();
        return fixture;
    }

    private static MetadataV2Graph BuildTypingGraph()
    {
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        var builder = new TestMetadataV2GraphBuilder();

        // Vector feature service -> FeatureServer.
        builder
            .AddResource("res-typing-vector", "Vector Layer", MetadataV2ResourceType.FeatureDataset,
                spatial: PointSpatial(), accessPolicy: anonymous)
            .AddService("svc-typing-vector", VectorServiceName,
                protocols: [ServiceProtocols.FeatureServer], accessPolicy: anonymous)
            .AddPublication("pub-typing-vector", "svc-typing-vector", "res-typing-vector",
                layerIndex: 700, serviceLocalId: "700",
                publicationType: MetadataV2PublicationType.EsriFeatureLayer);

        // Raster/coverage service -> ImageServer (must not flatten to FeatureServer).
        builder
            .AddResource("res-typing-raster", "Raster Layer", MetadataV2ResourceType.RasterDataset,
                accessPolicy: anonymous)
            .AddStorageBinding(
                "binding-typing-raster",
                "res-typing-raster",
                "typing_raster_data",
                storageType: MetadataV2StorageType.RelationalTable,
                storageLayerId: 701)
            .AddService("svc-typing-raster", RasterServiceName,
                protocols: [ServiceProtocols.ImageServer], accessPolicy: anonymous)
            .AddPublication("pub-typing-raster", "svc-typing-raster", "res-typing-raster",
                layerIndex: 701, serviceLocalId: "701",
                publicationType: MetadataV2PublicationType.EsriImageLayer);

        // Service reachable under multiple Esri types -> one entry per type.
        builder
            .AddResource("res-typing-multi", "Multi Layer", MetadataV2ResourceType.FeatureDataset,
                spatial: PointSpatial(), accessPolicy: anonymous)
            .AddService("svc-typing-multi", MultiTypeServiceName,
                protocols: [ServiceProtocols.FeatureServer, ServiceProtocols.MapServer],
                accessPolicy: anonymous)
            .AddPublication("pub-typing-multi", "svc-typing-multi", "res-typing-multi",
                layerIndex: 702, serviceLocalId: "702",
                publicationType: MetadataV2PublicationType.EsriFeatureLayer);

        // GPServer is service-scoped and must remain discoverable without a layer
        // publication, matching the default geoprocessing service seeded at startup.
        builder.AddService("svc-typing-gp", GpServiceName,
            protocols: [ServiceProtocols.GPServer], accessPolicy: anonymous);

        return builder.Build();
    }

    private static MetadataV2ResourceSpatial PointSpatial()
        => new()
        {
            GeometryType = MetadataV2GeometryType.Point,
            SpatialReference = new MetadataV2SpatialReference
            {
                Srid = 4326,
                Crs = "EPSG:4326",
                IsGeographic = true
            },
            PrimaryGeometryField = "shape"
        };
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GeoservicesCatalog)]
public sealed class GeoservicesCatalogServiceTypingTests : IClassFixture<GeoservicesCatalogServiceTypingTests.DefaultTypingFixture>
{
    private readonly WebAppFixture _fixture;

    public GeoservicesCatalogServiceTypingTests(DefaultTypingFixture fixture)
    {
        _fixture = fixture.Application;
    }

    public sealed class DefaultTypingFixture : IAsyncLifetime
    {
        public WebAppFixture Application { get; } = CreateFixture(BuildRasterStoreWithRasters());

        public Task InitializeAsync() => Application.InitializeAsync();

        public Task DisposeAsync() => Application.DisposeAsync();
    }

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
        var services = await GetServicesByNameAsync(_fixture, RasterServiceName);

        // The raster service exposes only the ImageServer protocol, so it must NOT
        // appear as a FeatureServer in the catalog.
        services.Should().NotBeEmpty();
        services.Should().Contain(type => type == "ImageServer");
        services.Should().NotContain(type => type == "FeatureServer");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_VectorService_TypedAsFeatureServer()
    {
        var services = await GetServicesByNameAsync(_fixture, VectorServiceName);

        services.Should().ContainSingle();
        services.Should().Contain(type => type == "FeatureServer");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_MultiProtocolService_ListedOncePerEsriType()
    {
        var services = await GetServicesByNameAsync(_fixture, MultiTypeServiceName);

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
        var services = await GetServicesByNameAsync(_fixture, GpServiceName);

        services.Should().ContainSingle().Which.Should().Be("GPServer");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_DeniedLayerlessGpServicesAlongsidePublicService_EmitsNoAuthChallengeHeaders()
    {
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        var authenticated = new AccessPolicy { AllowAnonymous = false };
        var builder = new TestMetadataV2GraphBuilder();
        builder
            .AddResource("res-public-vector", "Public Vector", MetadataV2ObjectType.FeatureDataset,
                fields: [new() { Name = "shape", Type = MetadataV2FieldType.Geometry }],
                spatial: PointSpatial(), accessPolicy: anonymous)
            .AddService("svc-public-vector", "public-vector",
                protocols: [ServiceProtocols.FeatureServer], accessPolicy: anonymous)
            .AddPublication("pub-public-vector", "svc-public-vector", "res-public-vector",
                layerIndex: 710, serviceLocalId: "710",
                publicationType: MetadataV2LayerType.EsriFeatureLayer);

        for (var i = 0; i < 3; i++)
        {
            builder.AddService($"svc-denied-gp-{i}", $"denied-gp-{i}",
                protocols: [ServiceProtocols.GPServer], accessPolicy: authenticated);
        }

        await using var fixture = await CreateAnonymousFixtureAsync(builder.Build());
        using var response = await fixture.Client.GetAsync("/rest/services?f=json");

        response.Be200Ok();
        response.Headers.TryGetValues("WWW-Authenticate", out _).Should().BeFalse();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = payload.RootElement.GetProperty("services").EnumerateArray()
            .Select(static entry => entry.GetProperty("name").GetString())
            .ToArray();
        names.Should().Contain("public-vector");
        names.Should().NotContain(static name => name != null && name.StartsWith("denied-gp-", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_OnlyDeniedLayerlessGpServices_EmitsSingleAuthChallengeHeader()
    {
        var authenticated = new AccessPolicy { AllowAnonymous = false };
        var builder = new TestMetadataV2GraphBuilder();
        for (var i = 0; i < 3; i++)
        {
            builder.AddService($"svc-only-denied-gp-{i}", $"only-denied-gp-{i}",
                protocols: [ServiceProtocols.GPServer], accessPolicy: authenticated);
        }

        await using var fixture = await CreateAnonymousFixtureAsync(builder.Build());
        using var response = await fixture.Client.GetAsync("/rest/services?f=json");

        await response.AssertGeoServicesErrorAsync(499);
        response.Headers.TryGetValues("WWW-Authenticate", out var challenges).Should().BeTrue();
        var challengeList = challenges!.ToArray();
        challengeList.Should().NotBeEmpty();
        challengeList.Should().OnlyHaveUniqueItems();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_OtherTenantLayerlessGpService_IsSkippedNotDenied()
    {
        var builder = new TestMetadataV2GraphBuilder();
        builder.AddService("svc-other-tenant-gp", "other-tenant-gp",
            protocols: [ServiceProtocols.GPServer], accessPolicy: new AccessPolicy { AllowAnonymous = true });
        var graph = builder.Build();
        graph = graph with
        {
            Services = graph.Services
                .Select(static service => service with { Metadata = service.Metadata with { Tenant = "tenant-b" } })
                .ToArray()
        };

        await using var fixture = await CreateAnonymousFixtureAsync(graph);
        using var response = await fixture.Client.GetAsync("/rest/services?f=json");

        response.Be200Ok();
        response.Headers.TryGetValues("WWW-Authenticate", out _).Should().BeFalse();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("folders").EnumerateArray()
            .Select(static folder => folder.GetString()).Should().Contain("Utilities");
        payload.RootElement.GetProperty("services").EnumerateArray()
            .Select(static entry => entry.GetProperty("name").GetString()).Should().NotContain("other-tenant-gp");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services")]
    public async Task PostSoapGetFolders_OnlyDeniedLayerlessGpServices_ReturnsAuthenticationFault()
    {
        var builder = new TestMetadataV2GraphBuilder();
        builder.AddService("svc-soap-denied-gp", "soap-denied-gp",
            protocols: [ServiceProtocols.GPServer], accessPolicy: new AccessPolicy { AllowAnonymous = false });

        await using var fixture = await CreateAnonymousFixtureAsync(builder.Build());
        const string envelope = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body><GetFolders xmlns="http://www.esri.com/schemas/ArcGIS/10.8" /></soap:Body>
            </soap:Envelope>
            """;
        using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        using var response = await fixture.Client.PostAsync("/services", content);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        response.Headers.TryGetValues("WWW-Authenticate", out _).Should().BeTrue();
        var payload = XDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.Descendants().Should().ContainSingle(element => element.Name.LocalName == "Fault");
        payload.Descendants().Should().NotContain(element => element.Name.LocalName == "FolderNames");
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
                .AddResource($"res-typing-duplicate-{suffix}", "Duplicate Layer", MetadataV2ObjectType.FeatureDataset,
                    fields: [new() { Name = "shape", Type = MetadataV2FieldType.Geometry }],
                    spatial: PointSpatial(), accessPolicy: anonymous)
                .AddService($"svc-typing-duplicate-{suffix}", DuplicateServiceName,
                    protocols: [ServiceProtocols.FeatureServer], accessPolicy: anonymous)
                .AddPublication($"pub-typing-duplicate-{suffix}", $"svc-typing-duplicate-{suffix}",
                    $"res-typing-duplicate-{suffix}", layerIndex: 710, serviceLocalId: "710",
                    publicationType: MetadataV2LayerType.EsriFeatureLayer);
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
        var fixture = CreateFixture(rasterStore, graph);
        await fixture.InitializeAsync();
        return fixture;
    }

    // Dev auth signs every request in as an administrator, and the public locator would keep a
    // denied-only directory at 200, so both are off for these anonymous callers.
    private static async Task<WebAppFixture> CreateAnonymousFixtureAsync(MetadataV2Graph graph)
    {
        var fixture = CreateFixture(BuildRasterStoreWithRasters(), graph).ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("Geocoding:Enabled", "false");
        });
        await fixture.InitializeAsync();
        return fixture;
    }

    private static WebAppFixture CreateFixture(
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

        return fixture;
    }

    private static MetadataV2Graph BuildTypingGraph()
    {
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        var builder = new TestMetadataV2GraphBuilder();

        // Vector feature service -> FeatureServer.
        builder
            .AddResource("res-typing-vector", "Vector Layer", MetadataV2ObjectType.FeatureDataset,
                fields: [new() { Name = "shape", Type = MetadataV2FieldType.Geometry }],
                spatial: PointSpatial(), accessPolicy: anonymous)
            .AddService("svc-typing-vector", VectorServiceName,
                protocols: [ServiceProtocols.FeatureServer], accessPolicy: anonymous)
            .AddPublication("pub-typing-vector", "svc-typing-vector", "res-typing-vector",
                layerIndex: 700, serviceLocalId: "700",
                publicationType: MetadataV2LayerType.EsriFeatureLayer);

        // Raster/coverage service -> ImageServer (must not flatten to FeatureServer).
        builder
            .AddResource("res-typing-raster", "Raster Layer", MetadataV2ObjectType.RasterDataset,
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
                publicationType: MetadataV2LayerType.EsriImageLayer);

        // Service reachable under multiple Esri types -> one entry per type.
        builder
            .AddResource("res-typing-multi", "Multi Layer", MetadataV2ObjectType.FeatureDataset,
                fields: [new() { Name = "shape", Type = MetadataV2FieldType.Geometry }],
                spatial: PointSpatial(), accessPolicy: anonymous)
            .AddService("svc-typing-multi", MultiTypeServiceName,
                protocols: [ServiceProtocols.FeatureServer, ServiceProtocols.MapServer],
                accessPolicy: anonymous)
            .AddPublication("pub-typing-multi", "svc-typing-multi", "res-typing-multi",
                layerIndex: 702, serviceLocalId: "702",
                publicationType: MetadataV2LayerType.EsriFeatureLayer);

        // GPServer is service-scoped and must remain discoverable without a layer
        // publication, matching the default geoprocessing service seeded at startup.
        builder.AddService("svc-typing-gp", GpServiceName,
            protocols: [ServiceProtocols.GPServer], accessPolicy: anonymous);

        return builder.Build();
    }

    private static MetadataV2ObjectSpatial PointSpatial()
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

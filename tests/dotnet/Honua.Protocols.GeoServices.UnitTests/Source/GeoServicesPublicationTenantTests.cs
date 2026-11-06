// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Validation;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Tiles;
using Honua.Protocols.GeoServices.MapServer;
using Honua.Protocols.GeoServices.MapServer.Tiles;
using Honua.Protocols.GeoServices.VectorTileServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

public sealed class GeoServicesPublicationTenantTests
{
    [UnitTheory]
    [Protocol(TestProtocols.MapServer)]
    [InlineData(null, false)]
    [InlineData("tenant-a", false)]
    [InlineData("tenant-b", true)]
    public async Task MapMetadata_PrivatePublication_OnlyOwnerSeesLayer(string? tenantId, bool allowed)
    {
        using var services = CreateServices(tenantId, Substitute.For<ITileProvider>());
        var context = Context(services);
        var result = await InvokeAsync(typeof(MapServerEndpoints), "HandleGetServiceMetadata", [context]);
        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        body.Should().Contain("public-dataset");
        if (allowed)
        {
            body.Should().Contain("private-dataset");
        }
        else
        {
            body.Should().NotContain("private-dataset");
        }
    }

    [UnitTheory]
    [Protocol(TestProtocols.VectorTileServer)]
    [InlineData(null, 1)]
    [InlineData("tenant-a", 1)]
    [InlineData("tenant-b", 2)]
    public async Task VectorTile_PrivatePrimary_SelectsCallerVisibleSource(string? tenantId, int expectedStorageId)
    {
        var tiles = Substitute.For<ITileProvider>();
        tiles.GetMvtTileAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<FeatureQuery?>(), Arg.Any<TileOptions>(), Arg.Any<TileLimits>(),
                Arg.Any<GridGeometry?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<byte[]?>(null));
        using var services = CreateServices(tenantId, tiles);
        var context = Context(services);
        var result = await InvokeAsync(typeof(VectorTileServerEndpoints), "HandleGetTile",
            [context, 0, 0, 0, CancellationToken.None]);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        var reads = tiles.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ITileProvider.GetMvtTileAsync));
        reads.Should().ContainSingle().Which.GetArguments()[0].Should().Be(expectedStorageId);
    }

    [UnitTheory]
    [Protocol(TestProtocols.MapServer)]
    [InlineData(null)]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public async Task MapTile_PrivatePublication_DoesNotReadForeignFeatures(string? tenantId)
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(0, []));
        using var services = CreateServices(tenantId, Substitute.For<ITileProvider>(), reader);
        var context = Context(services);
        context.Request.RouteValues["z"] = "0";
        context.Request.RouteValues["y"] = "0";
        context.Request.RouteValues["x"] = "0";

        var result = await InvokeAsync(typeof(MapServerEndpoints), "HandleTile", [context]);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var readIds = reader.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IFeatureReader.QueryAsync))
            .Select(call => (int)call.GetArguments()[0]!);
        int[] expected = tenantId == "tenant-b" ? [1, 2] : [1];
        readIds.Should().BeEquivalentTo(expected);
    }

    [UnitTheory]
    [Protocol(TestProtocols.MapServer)]
    [InlineData(null, 1)]
    [InlineData("tenant-a", 1)]
    [InlineData("tenant-b", 2)]
    public void QueuedMapExport_CollidingPublicLayerId_RetainsSubmitterVisibleSource(string? tenantId, int expectedStorageId)
    {
        var graph = CreateGraph();
        graph = graph with
        {
            Publications = graph.Publications.Select(publication => publication with { LayerIndex = 11 }).ToArray()
        };
        MetadataV2GraphValidator.Validate(graph).IsValid.Should().BeTrue();
        var snapshot = new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow);
        using var scope = JobSecurityScope.Begin(new JobSecurityContext("submitter", tenantId, []));

        var layers = MapTileExportLayerResolver.BuildRenderLayers(snapshot, graph.Services[0],
            [new TileExportMapLayerSelection("11", "default", 0)], out _);

        layers.Should().ContainSingle().Which.LayerId.Should().Be(expectedStorageId);
    }

    private static ServiceProvider CreateServices(string? tenantId, ITileProvider tiles, IFeatureReader? featureReader = null)
    {
        var graph = CreateGraph();
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(tenantId);
        return new ServiceCollection().AddLogging().AddValidationServices()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph))
            .AddSingleton(tenant).AddSingleton(tiles)
            .AddSingleton<IFeatureReader>(featureReader ?? Substitute.For<IFeatureReader>())
            .AddSingleton<ILayerStyleCatalog>(Substitute.For<ILayerStyleCatalog>())
            .AddSingleton<RasterRenderCapacityLimiter>()
            .AddSingleton<ICoordinateTransformService>(new WellKnownCoordinateTransformService())
            .BuildServiceProvider();
    }

    private static MetadataV2Graph CreateGraph()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("public", "public-dataset", accessPolicy: new() { AllowAnonymous = true })
            .AddResource("private", "private-dataset", accessPolicy: new() { AllowAnonymous = true })
            .AddStorageBinding("public-binding", "public", "public", storageLayerId: 1)
            .AddStorageBinding("private-binding", "private", "private", storageLayerId: 2)
            .AddService("maps", "maps", protocols: [ServiceProtocols.MapServer, ServiceProtocols.VectorTileServer],
                accessPolicy: new() { AllowAnonymous = true })
            .AddPublication("public-pub", "maps", "public", layerIndex: 11, storageBindingId: "public-binding",
                publicationType: MetadataV2PublicationType.EsriMapLayer)
            .AddPublication("private-pub", "maps", "private", layerIndex: 22, storageBindingId: "private-binding",
                publicationType: MetadataV2PublicationType.EsriMapLayer, isPrimary: true)
            .Build();
        return graph with
        {
            Resources = graph.Resources.Select(resource => resource with
            {
                Spatial = new MetadataV2ResourceSpatial
                {
                    SpatialReference = MetadataV2SpatialReference.Wgs84,
                    GeometryType = MetadataV2GeometryType.Point,
                    Bbox = new MetadataV2Bbox { West = 0, South = 0, East = 1, North = 1 }
                }
            }).ToArray(),
            Publications = graph.Publications.Select(publication => publication.Metadata.Id == "private-pub"
                ? publication with { Metadata = publication.Metadata with { Tenant = "tenant-b" } }
                : publication).ToArray()
        };
    }

    private static DefaultHttpContext Context(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.RouteValues["serviceId"] = "maps";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("honua.example");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<IResult> InvokeAsync(Type endpointType, string methodName, object[] arguments)
    {
        var method = endpointType.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)!;
        return await (Task<IResult>)method.Invoke(null, arguments)!;
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Classic.Wcs20;
using Honua.Protocols.Ogc.Classic.Wms;
using Honua.Protocols.Ogc.Classic.Wmts;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic;

public sealed class MetadataV2LifecycleRoutingTests
{
    [UnitTest]
    public void WmsAndWmtsResolvers_WithRetiredPublication_OmitLayer()
    {
        var snapshot = CreateSnapshot(publicationLifecycle: MetadataV2LifecycleStatus.Retired);
        var service = snapshot.Graph.Services.Single();

        InvokeLayerResolver(typeof(WmsRequestHandlers), "ResolveWmsLayers", snapshot, service)
            .Length.Should().Be(0);
        InvokeLayerResolver(typeof(WmtsRequestHandlers), "ResolveWmtsLayers", snapshot, service)
            .Length.Should().Be(0);
    }

    [UnitTest]
    public void WmsAndWmtsResolvers_WithServingGraph_IncludeLayer()
    {
        var snapshot = CreateSnapshot(publicationLifecycle: MetadataV2LifecycleStatus.Active);
        var service = snapshot.Graph.Services.Single();

        InvokeLayerResolver(typeof(WmsRequestHandlers), "ResolveWmsLayers", snapshot, service)
            .Length.Should().Be(1);
        InvokeLayerResolver(typeof(WmtsRequestHandlers), "ResolveWmtsLayers", snapshot, service)
            .Length.Should().Be(1);
    }

    [UnitTest]
    public void WmsAndWmtsResolvers_WithRetiredService_OmitLayer()
    {
        var snapshot = CreateSnapshot(
            publicationLifecycle: MetadataV2LifecycleStatus.Active,
            serviceLifecycle: MetadataV2LifecycleStatus.Retired);
        var service = snapshot.Graph.Services.Single();

        InvokeLayerResolver(typeof(WmsRequestHandlers), "ResolveWmsLayers", snapshot, service)
            .Length.Should().Be(0);
        InvokeLayerResolver(typeof(WmtsRequestHandlers), "ResolveWmtsLayers", snapshot, service)
            .Length.Should().Be(0);
    }

    [UnitTest]
    public async Task WcsLayerResolver_WithRetiredBinding_OmitsCoverage()
    {
        var snapshot = CreateSnapshot(
            publicationLifecycle: MetadataV2LifecycleStatus.Active,
            bindingLifecycle: MetadataV2LifecycleStatus.Retired);
        var method = typeof(Wcs20Handler).GetMethod(
            "ResolveResourceForLayerAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var parameters = method!.GetParameters();
        parameters.Length.Should().Be(4);
        parameters[0].Name.Should().Be("context");
        parameters[1].Name.Should().Be("snapshot");
        parameters[2].Name.Should().Be("layerId");
        parameters[3].Name.Should().Be("cancellationToken");

        var task = (Task<(bool Found, MetadataV2Object? Resource, MetadataV2Service? OwningService)>)method.Invoke(
            null,
            [new DefaultHttpContext(), snapshot, 7, CancellationToken.None])!;
        var (resolved, resource, owningService) = await task;

        resolved.Should().BeFalse();
        resource.Should().BeNull("the resource behind the retired binding must not leak to the coverage route");
        owningService.Should().BeNull("the service owning the retired binding must not leak to the coverage route");
    }

    [UnitTest]
    public async Task WcsServiceResolver_WithRetiredService_RejectsRoute()
    {
        var snapshot = CreateSnapshot(
            publicationLifecycle: MetadataV2LifecycleStatus.Active,
            serviceLifecycle: MetadataV2LifecycleStatus.Retired);
        var method = typeof(Wcs20Handler).GetMethod(
            "ResolveServiceAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var task = (Task)method!.Invoke(
            null,
            [new DefaultHttpContext(), snapshot, new Wcs20RouteScope(null, "service-ogc"), CancellationToken.None])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task);

        result.Should().NotBeNull();
        result!.GetType().GetProperty("Service")!.GetValue(result).Should().BeNull();
        result.GetType().GetProperty("Error")!.GetValue(result).Should().NotBeNull();
    }

    private static Array InvokeLayerResolver(
        Type handlerType,
        string methodName,
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        var method = handlerType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        return (Array)method!.Invoke(null, [snapshot, service, new DefaultHttpContext()])!;
    }

    private static MetadataV2GraphSnapshot CreateSnapshot(
        MetadataV2LifecycleStatus publicationLifecycle,
        MetadataV2LifecycleStatus resourceLifecycle = MetadataV2LifecycleStatus.Active,
        MetadataV2LifecycleStatus bindingLifecycle = MetadataV2LifecycleStatus.Active,
        MetadataV2LifecycleStatus serviceLifecycle = MetadataV2LifecycleStatus.Active)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-ogc", Name = "ogc" },
            // Explicit lifecycle. An omitted status is active; these cases pass the
            // service lifecycle under test so a retired service is not confused with
            // that default.
            Status = new MetadataV2Status { Lifecycle = serviceLifecycle },
            Protocols = ["WCS"]
        };
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-7", Name = "layer-7" },
            Type = MetadataV2ObjectType.FeatureDataset,
            StorageBindingIds = ["binding-7"],
            PrimaryStorageBindingId = "binding-7",
            Spatial = new MetadataV2ObjectSpatial { GeometryType = MetadataV2GeometryType.Point },
            Status = new MetadataV2Status { Lifecycle = resourceLifecycle }
        };
        var binding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-7", Name = "binding-7" },
            ResourceId = resource.Metadata.Id,
            StorageLayerId = 7,
            Status = new MetadataV2Status { Lifecycle = bindingLifecycle }
        };
        var publication = new MetadataV2Layer
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication-7", Name = "layer-7" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = binding.Metadata.Id,
            Identifier = new MetadataV2LayerIdentifier { Value = "7", IsNumeric = true },
            Status = new MetadataV2Status { Lifecycle = publicationLifecycle }
        };
        var graph = new MetadataV2Graph
        {
            Resources = [resource],
            StorageBindings = [binding],
            Services = [service],
            Publications = [publication]
        };
        return new MetadataV2GraphSnapshot(graph, "\"test\"", DateTimeOffset.UtcNow);
    }
}

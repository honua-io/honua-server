// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Classic.Wcs20;
using Honua.Protocols.Ogc.Classic.Wms;
using Honua.Protocols.Ogc.Classic.Wmts;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic;

/// <summary>
/// Regression coverage for the shared Metadata v2 retirement boundary used by the
/// classic OGC adapters.
/// </summary>
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
    public void WcsLayerResolver_WithRetiredBinding_OmitsCoverage()
    {
        var snapshot = CreateSnapshot(
            publicationLifecycle: MetadataV2LifecycleStatus.Active,
            bindingLifecycle: MetadataV2LifecycleStatus.Retired);
        var method = typeof(Wcs20Handler).GetMethod(
            "TryResolveResourceForLayer",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        object?[] arguments = [snapshot, 7, null];

        var resolved = (bool)method!.Invoke(null, arguments)!;

        resolved.Should().BeFalse();
        arguments[2].Should().BeNull();
    }

    private static Array InvokeLayerResolver(
        Type handlerType,
        string methodName,
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        var method = handlerType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        return (Array)method!.Invoke(null, [snapshot, service])!;
    }

    private static MetadataV2GraphSnapshot CreateSnapshot(
        MetadataV2LifecycleStatus publicationLifecycle,
        MetadataV2LifecycleStatus resourceLifecycle = MetadataV2LifecycleStatus.Active,
        MetadataV2LifecycleStatus bindingLifecycle = MetadataV2LifecycleStatus.Active)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-ogc", Name = "ogc" }
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-7", Name = "layer-7" },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = ["binding-7"],
            PrimaryStorageBindingId = "binding-7",
            Spatial = new MetadataV2ResourceSpatial { GeometryType = MetadataV2GeometryType.Point },
            Status = new MetadataV2Status { Lifecycle = resourceLifecycle }
        };
        var binding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-7", Name = "binding-7" },
            ResourceId = resource.Metadata.Id,
            StorageLayerId = 7,
            Status = new MetadataV2Status { Lifecycle = bindingLifecycle }
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication-7", Name = "layer-7" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = binding.Metadata.Id,
            Identifier = new MetadataV2PublicationIdentifier { Value = "7", IsNumeric = true },
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

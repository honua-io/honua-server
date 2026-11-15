// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private sealed record PublishedLayerGraphPlan
    {
        public required MetadataV2Graph Graph { get; init; }
        public required LayerPublishRequest Request { get; init; }
        public required string ServiceName { get; init; }
        public required int LayerId { get; init; }
        public required PublishedLayerStorage Storage { get; init; }
        public required string PrimaryKey { get; init; }
        public string? GeometryColumn { get; init; }
        public required string GeometryType { get; init; }
        public required int Srid { get; init; }
        public double? StorageCoordinateEpoch { get; init; }
        public required IReadOnlyList<LayerFieldInsert> Fields { get; init; }
        public LayerExtentInsert? Extent { get; init; }
        public required IReadOnlyList<string> Capabilities { get; init; }
        public required PublicationScope Scope { get; init; }
        public required DateTimeOffset GeneratedAt { get; init; }
        public IReadOnlyList<MetadataV2Object> StyleResources { get; init; } = [];
        public IReadOnlyList<string> StyleResourceIds { get; init; } = [];
    }

    private sealed record PublicationGraphObjects
    {
        public required MetadataV2Service Service { get; init; }
        public required MetadataV2Object Resource { get; init; }
        public required MetadataV2StorageBinding Binding { get; init; }
        public required MetadataV2Layer Feature { get; init; }
        public MetadataV2Layer? Stac { get; init; }
    }

    private MetadataV2Graph BuildPublishedLayerGraph(PublishedLayerGraphPlan plan)
    {
        var graph = plan.Graph;
        var service = BuildPublishedService(graph, plan.ServiceName, plan.Srid, plan.GeneratedAt);
        service = service with { Metadata = ApplyPublicationScope(service.Metadata, plan.Scope) };
        var organizedGraph = PublishedImportTarget.PrepareFolder(graph, plan.Request, service.Metadata.Tenant);
        service = PublishedImportTarget.BindService(organizedGraph, service, plan.Request);
        service = PublishedServiceOrganization.Apply(organizedGraph, service, plan.Request);
        var objects = BuildPublicationGraphObjects(plan, service);
        var updatedGraph = AssemblePublicationGraph(plan, objects, organizedGraph);
        var validation = MetadataV2GraphValidator.Validate(updatedGraph);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Published layer metadata v2 graph is invalid: {string.Join("; ", validation.Errors)}");
        }
        return updatedGraph;
    }

    private PublicationGraphObjects BuildPublicationGraphObjects(PublishedLayerGraphPlan plan, MetadataV2Service service)
    {
        var resource = BuildPublicationResource(plan);
        var binding = BuildPublishedStorageBinding(plan.Request, plan.Storage,
            new PublishedStorageBindingContext(plan.LayerId, resource.Metadata.Id, plan.GeneratedAt)
            {
                TemporalColumn = ResolveTemporalColumnForPublish(plan.Request.TemporalColumn, plan.Fields)
            });
        binding = binding with { Metadata = ApplyPublicationScope(binding.Metadata, plan.Scope) };
        var objects = (Service: service, Resource: resource, Binding: binding);
        var feature = BuildPlannedPublication(plan, objects, MetadataV2LayerType.EsriFeatureLayer);
        var stac = string.IsNullOrWhiteSpace(plan.Storage.GeometryColumn)
            ? null : BuildPlannedPublication(plan, objects, MetadataV2LayerType.StacCollection);
        service = service with
        {
            PublicationIds = service.PublicationIds.Append(feature.Metadata.Id)
                .Concat(stac is null ? [] : new[] { stac.Metadata.Id })
                .Distinct(StringComparer.Ordinal).ToArray()
        };
        return new PublicationGraphObjects
        {
            Service = service, Resource = resource, Binding = binding, Feature = feature, Stac = stac
        };
    }

    private MetadataV2Object BuildPublicationResource(PublishedLayerGraphPlan plan)
    {
        // Managed storage belongs to the server, even when the source route has a connection ID.
        var connectionId = plan.Storage.IsManagedStore ? null : plan.Request.ConnectionId;
        var resource = BuildPublishedResource(plan.Request, plan.LayerId, connectionId,
            plan.PrimaryKey, plan.GeometryColumn, plan.GeometryType, plan.Srid, plan.Storage.StorageSrid,
            plan.StorageCoordinateEpoch, plan.Fields, plan.Extent, plan.GeneratedAt);
        resource = resource with { Metadata = ApplyPublicationScope(resource.Metadata, plan.Scope) };
        return plan.StyleResourceIds.Count > 0 ? resource with { StyleResourceIds = plan.StyleResourceIds } : resource;
    }

    private MetadataV2Layer BuildPlannedPublication(PublishedLayerGraphPlan plan,
        (MetadataV2Service Service, MetadataV2Object Resource, MetadataV2StorageBinding Binding) objects, MetadataV2LayerType layerType)
    {
        var primary = layerType == MetadataV2LayerType.EsriFeatureLayer;
        var connectionId = plan.Storage.IsManagedStore ? null : plan.Request.ConnectionId;
        var publication = BuildPublishedPublication(objects.Service, objects.Resource, objects.Binding,
            plan.LayerId.ToString(CultureInfo.InvariantCulture), BuildLayerGraphId("layer", plan.LayerId, connectionId),
            plan.Request.LayerName.Trim(), layerType,
            primary, primary ? "pub" : "pub-stac", plan.Request.Enabled, plan.GeneratedAt,
            primary ? plan.Capabilities : null);
        return publication with { Metadata = ApplyPublicationScope(publication.Metadata, plan.Scope) };
    }

    private MetadataV2Graph AssemblePublicationGraph(PublishedLayerGraphPlan plan, PublicationGraphObjects objects,
        MetadataV2Graph organizedGraph)
    {
        var graph = plan.Graph;
        var connection = BuildPlannedConnection(plan);
        return graph with
        {
            Revision = Math.Max(graph.Revision + 1, 1),
            GeneratedAt = plan.GeneratedAt,
            Folders = organizedGraph.Folders,
            Services = UpsertById(graph.Services, objects.Service, static item => item.Metadata.Id),
            Resources = BuildPlannedResources(plan, objects.Resource),
            StorageBindings = UpsertById(graph.StorageBindings, objects.Binding, static item => item.Metadata.Id),
            Publications = objects.Stac is null
                ? UpsertPublication(graph.Publications, objects.Feature)
                : UpsertPublication(UpsertPublication(graph.Publications, objects.Feature), objects.Stac),
            Connections = connection is null ? graph.Connections
                : UpsertById(graph.Connections, connection, static item => item.Metadata.Id)
        };
    }

    private static IReadOnlyList<MetadataV2Object> BuildPlannedResources(PublishedLayerGraphPlan plan, MetadataV2Object resource)
    {
        var resources = UpsertById(plan.Graph.Resources, resource, static item => item.Metadata.Id);
        foreach (var styleResource in plan.StyleResources)
        {
            var scopedStyle = styleResource with
            {
                Metadata = PreserveDependencyMetadata(
                    plan.Graph.Resources.FirstOrDefault(candidate => candidate.Metadata.Id == styleResource.Metadata.Id)?.Metadata,
                    styleResource.Metadata, plan.Scope)
            };
            resources = UpsertById(resources, scopedStyle, static item => item.Metadata.Id);
        }
        return resources;
    }

    private static MetadataV2Connection? BuildPlannedConnection(PublishedLayerGraphPlan plan)
    {
        var connectionId = plan.Storage.IsManagedStore ? null : plan.Request.ConnectionId;
        var connection = BuildPublishedConnection(connectionId, plan.GeneratedAt);
        return connection is null ? null : connection with
        {
            Metadata = PreserveDependencyMetadata(
                plan.Graph.Connections.FirstOrDefault(candidate => candidate.Metadata.Id == connection.Metadata.Id)?.Metadata,
                connection.Metadata, plan.Scope)
        };
    }
}

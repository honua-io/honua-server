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

    private MetadataV2Graph BuildPublishedLayerGraph(PublishedLayerGraphPlan plan)
    {
        var graph = plan.Graph;
        var request = plan.Request;
        var layerId = plan.LayerId;
        var storage = plan.Storage;
        var resourcePrimaryKeyColumn = plan.PrimaryKey;
        var resourceGeometryColumn = plan.GeometryColumn;
        var geometryType = plan.GeometryType;
        var srid = plan.Srid;
        var storageCoordinateEpoch = plan.StorageCoordinateEpoch;
        var fields = plan.Fields;
        var extent = plan.Extent;
        var capabilities = plan.Capabilities;
        var publicationScope = plan.Scope;
        var now = plan.GeneratedAt;
        var styleResources = plan.StyleResources;
        var styleResourceIds = plan.StyleResourceIds;
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);
        // Managed storage belongs to the server, even when the source route has a connection ID.
        var storageConnectionId = storage.IsManagedStore ? null : request.ConnectionId;
        var graphLayerKey = BuildLayerGraphId("layer", layerId, storageConnectionId);
        var service = BuildPublishedService(graph, plan.ServiceName, srid, now);
        service = service with { Metadata = ApplyPublicationScope(service.Metadata, publicationScope) };
        var organizedGraph = PublishedImportTarget.PrepareFolder(graph, request, service.Metadata.Tenant);
        service = PublishedImportTarget.BindService(organizedGraph, service, request);
        service = PublishedServiceOrganization.Apply(organizedGraph, service, request);
        var resource = BuildPublishedResource(
            request,
            layerId,
            storageConnectionId,
            resourcePrimaryKeyColumn,
            resourceGeometryColumn,
            geometryType,
            srid,
            storage.StorageSrid,
            storageCoordinateEpoch,
            fields,
            extent,
            now);
        var binding = BuildPublishedStorageBinding(
            request,
            storage,
            new PublishedStorageBindingContext(layerId, resource.Metadata.Id, now)
            {
                TemporalColumn = ResolveTemporalColumnForPublish(request.TemporalColumn, fields)
            });
        var featurePublication = BuildPublishedPublication(
            service,
            resource,
            binding,
            layerIdText,
            graphLayerKey,
            request.LayerName.Trim(),
            MetadataV2LayerType.EsriFeatureLayer,
            isPrimary: true,
            idPrefix: "pub",
            request.Enabled,
            now,
            capabilities);
        var stacPublication = string.IsNullOrWhiteSpace(storage.GeometryColumn) ? null : BuildPublishedPublication(
            service,
            resource,
            binding,
            layerIdText,
            graphLayerKey,
            request.LayerName.Trim(),
            MetadataV2LayerType.StacCollection,
            isPrimary: false,
            idPrefix: "pub-stac",
            request.Enabled,
            now);
        var connection = BuildPublishedConnection(storageConnectionId, now);
        if (connection is not null)
        {
            connection = connection with
            {
                Metadata = PreserveDependencyMetadata(
                    graph.Connections.FirstOrDefault(candidate => candidate.Metadata.Id == connection.Metadata.Id)?.Metadata,
                    connection.Metadata,
                    publicationScope)
            };
        }
        resource = resource with { Metadata = ApplyPublicationScope(resource.Metadata, publicationScope) };
        binding = binding with { Metadata = ApplyPublicationScope(binding.Metadata, publicationScope) };
        featurePublication = featurePublication with { Metadata = ApplyPublicationScope(featurePublication.Metadata, publicationScope) };
        if (stacPublication is not null)
        {
            stacPublication = stacPublication with { Metadata = ApplyPublicationScope(stacPublication.Metadata, publicationScope) };
        }
        service = service with
        {
            PublicationIds = service.PublicationIds
                .Append(featurePublication.Metadata.Id)
                .Concat(stacPublication is null ? [] : new[] { stacPublication.Metadata.Id })
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };

        if (styleResourceIds.Count > 0)
        {
            resource = resource with { StyleResourceIds = styleResourceIds };
        }

        var resourcesWithStyles = UpsertById(graph.Resources, resource, static item => item.Metadata.Id);
        // codeql[cs/linq/missed-select]: folds each style into the graph with UpsertById and preserved dependency metadata
        foreach (var styleResource in styleResources)
        {
            var scopedStyle = styleResource with
            {
                Metadata = PreserveDependencyMetadata(
                    graph.Resources.FirstOrDefault(candidate => candidate.Metadata.Id == styleResource.Metadata.Id)?.Metadata,
                    styleResource.Metadata,
                    publicationScope)
            };
            resourcesWithStyles = UpsertById(resourcesWithStyles, scopedStyle, static item => item.Metadata.Id);
        }

        var updatedGraph = graph with
        {
            Revision = Math.Max(graph.Revision + 1, 1),
            GeneratedAt = now,
            Folders = organizedGraph.Folders,
            Services = UpsertById(graph.Services, service, static item => item.Metadata.Id),
            Resources = resourcesWithStyles,
            StorageBindings = UpsertById(graph.StorageBindings, binding, static item => item.Metadata.Id),
            Publications = stacPublication is null
                ? UpsertPublication(graph.Publications, featurePublication)
                : UpsertPublication(
                UpsertPublication(graph.Publications, featurePublication),
                stacPublication),
            Connections = connection is null
                ? graph.Connections
                : UpsertById(graph.Connections, connection, static item => item.Metadata.Id)
        };

        var validation = MetadataV2GraphValidator.Validate(updatedGraph);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Published layer metadata v2 graph is invalid: {string.Join("; ", validation.Errors)}");
        }

        return updatedGraph;
    }
}

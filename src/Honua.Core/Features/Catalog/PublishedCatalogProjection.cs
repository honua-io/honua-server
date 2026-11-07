// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Services;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Portal.Domain;

namespace Honua.Core.Features.Catalog;

public sealed class PublishedCatalogProjection(PublishedMetadataProjection metadataProjection, ICoordinateTransformService coordinateTransform)
{
    public async Task<IReadOnlyList<PublishedCatalogItem>> ProjectAsync(
        PublishedMetadataRequest request, IReadOnlyList<PublishedCatalogProtocol> protocols, CancellationToken cancellationToken)
    {
        var items = new List<PublishedCatalogItem>();
        var snapshot = request.Snapshot;
        foreach (var service in snapshot.Index.ServicesById.Values)
        {
            var published = metadataProjection.ProjectDiscoverableService(request, service);
            if (published is null)
            {
                continue;
            }
            var layers = new List<PublishedCatalogItem>();
            foreach (var group in published.Publications.GroupBy(publication => publication.ResourceId, StringComparer.Ordinal))
            {
                var publication = group.FirstOrDefault(candidate => candidate.IsPrimary) ?? group.First();
                var resource = snapshot.ResolveResource(publication)!;
                layers.Add(ProjectMetadata(resource.Metadata) with
                {
                    Id = publication.Metadata.Id, Type = "layer", Title = publication.TitleOverride ?? resource.Metadata.Title ?? resource.Metadata.Name,
                    ServiceId = service.Metadata.Id, Folder = MetadataV2ServicePaths.FolderName(snapshot.Graph, service),
                    Access = metadataProjection.DeriveAccessTier(resource.AccessPolicy, service.AccessPolicy),
                    Url = PublishedMetadataProjection.PublicationUrl(request, service, publication),
                    Extent = await ProjectExtentAsync(resource, cancellationToken).ConfigureAwait(false),
                    TemporalExtent = resource.Temporal?.Extent,
                    Endpoints = Links(request, service, group.ToArray(), protocols)
                });
            }
            var serviceItem = ProjectMetadata(service.Metadata) with
            {
                Type = "service", Title = service.Metadata.Title ?? service.EffectivePublicName,
                Folder = MetadataV2ServicePaths.FolderName(snapshot.Graph, service), Access = published.Access, Url = published.Url,
                Layers = layers.Select(layer => layer.Id).ToArray(),
                Endpoints = Links(request, service, published.Publications, protocols),
                Extent = UnionExtent(layers), TemporalExtent = UnionTime(layers)
            };
            items.Add(serviceItem);
            items.AddRange(layers);
        }
        AddFolders(items, snapshot);
        return items;
    }

    private static PublishedCatalogItem ProjectMetadata(MetadataV2ObjectMetadata metadata) => new()
    {
        Id = metadata.Id, Type = "layer", Title = metadata.Title ?? metadata.Name, Owner = PublishedMetadataProjection.ResolveOwner(metadata),
        Access = PortalAccessLevels.Private, Description = metadata.Description, Created = metadata.CreatedAt, Updated = metadata.UpdatedAt ?? metadata.CreatedAt,
        Tags = metadata.Keywords.Concat(metadata.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
    };

    private async Task<MetadataV2Bbox?> ProjectExtentAsync(MetadataV2Resource resource, CancellationToken cancellationToken)
    {
        if (resource.Spatial?.Bbox is not { } bbox || resource.ReadSrid() is not { } srid)
        {
            return null;
        }
        if (srid == 4326)
        {
            return bbox;
        }
        var transformed = await coordinateTransform.TransformExtentAsync(
            bbox.West, bbox.South, bbox.East, bbox.North, srid, 4326, cancellationToken).ConfigureAwait(false);
        return transformed is { } extent
            ? new() { West = extent.MinX, South = extent.MinY, East = extent.MaxX, North = extent.MaxY } : null;
    }

    private static PublishedCatalogLink[] Links(PublishedMetadataRequest request, MetadataV2Service service,
        IReadOnlyList<MetadataV2Publication> publications, IReadOnlyList<PublishedCatalogProtocol> protocols)
    {
        var links = new List<PublishedCatalogLink>();
        foreach (var endpoint in protocols.Where(endpoint => ServiceProtocols.IsProtocolEnabled(service, endpoint.Protocol)))
        {
            var template = endpoint.HrefTemplate;
            var eligible = publications.Where(publication => !endpoint.PreferredPublicationRequired
                || ServiceProtocols.IsPreferredPublicationType(endpoint.Protocol, publication.PublicationType));
            var candidates = template.Contains("{collectionId}", StringComparison.Ordinal) ? eligible : eligible.Take(1);
            foreach (var publication in candidates)
            {
                var collectionId = publication.Metadata.Id;
                var path = template.Replace("{servicePath}", MetadataV2ServicePaths.GeoServicesPath(request.Snapshot.Graph, service), StringComparison.Ordinal)
                    .Replace("{serviceId}", Uri.EscapeDataString(service.Metadata.Name), StringComparison.Ordinal)
                    .Replace("{collectionId}", Uri.EscapeDataString(collectionId), StringComparison.Ordinal);
                links.Add(new(endpoint.Protocol, request.BaseUrl.TrimEnd('/') + path));
            }
        }
        return links.Distinct().ToArray();
    }

    private static MetadataV2Bbox? UnionExtent(IEnumerable<PublishedCatalogItem> items)
    {
        var extents = items.Where(item => item.Extent is not null).Select(item => item.Extent!).ToArray();
        if (extents.Length == 0)
        {
            return null;
        }
        var union = extents.Select(extent => BoundingBox.Create(extent.West, extent.South, extent.East, extent.North, 4326))
            .Aggregate((left, right) => left.Union(right));
        return new() { West = union.MinX, South = union.MinY, East = union.MaxX, North = union.MaxY };
    }

    private static MetadataV2TimeRange? UnionTime(IEnumerable<PublishedCatalogItem> items)
    {
        var extents = items.Where(item => item.TemporalExtent is not null).Select(item => item.TemporalExtent!).ToArray();
        return extents.Length == 0 ? null : new()
        {
            Start = extents.Any(extent => extent.Start is null) ? null : extents.Min(extent => extent.Start),
            End = extents.Any(extent => extent.End is null) ? null : extents.Max(extent => extent.End)
        };
    }

    private static void AddFolders(List<PublishedCatalogItem> items, MetadataV2GraphSnapshot snapshot)
    {
        var visibleServices = items.Where(item => item.Type == "service").ToArray();
        foreach (var folder in snapshot.Graph.Folders.DistinctBy(folder => folder.Metadata.Id))
        {
            var children = visibleServices.Where(item =>
                snapshot.Index.ServicesById[item.Id].FolderId == folder.Metadata.Id).ToArray();
            if (children.Length == 0)
            {
                continue;
            }
            items.Add(ProjectMetadata(folder.Metadata) with
            {
                Type = "folder", Folder = folder.Metadata.Name,
                Access = children.Any(child => child.Access == PortalAccessLevels.Public) ? PortalAccessLevels.Public
                    : children.Any(child => child.Access == PortalAccessLevels.Org) ? PortalAccessLevels.Org : PortalAccessLevels.Private,
                Extent = UnionExtent(children), TemporalExtent = UnionTime(children)
            });
        }
    }
}

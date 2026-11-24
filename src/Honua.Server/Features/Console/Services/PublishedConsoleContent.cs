// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Crosswalk;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Services;
using Honua.Core.Features.Portal.Domain;
using Honua.Infrastructure.Helpers;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Console.TypeSidecars;

namespace Honua.Server.Features.Console.Services;

internal sealed class PublishedConsoleContent(
    IMetadataV2GraphProvider graphProvider,
    PublishedMetadataProjection projection,
    IHttpContextAccessor contextAccessor)
{
    private Task<PublishedConsoleSnapshot>? _snapshot;

    public Task<PublishedConsoleSnapshot> ReadAsync(CancellationToken cancellationToken)
        => _snapshot ??= ReadCurrentAsync(cancellationToken);

    private async Task<PublishedConsoleSnapshot> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var ownedIds = snapshot.Graph.Services.Select(service => service.Metadata.Id)
            .Concat(snapshot.Graph.Publications.Select(publication => publication.Metadata.Id))
            .Concat(snapshot.Graph.Objects.Where(IsDatasetIdentity).Select(dataset => dataset.Metadata.Id))
            .ToHashSet(StringComparer.Ordinal);
        var items = new Dictionary<string, ConsoleContentItem>(StringComparer.Ordinal);
        var context = contextAccessor.HttpContext;
        if (context is null)
        {
            return new(ownedIds, items);
        }

        var request = new PublishedMetadataRequest(snapshot, context.User, BaseUrlResolver.GetBaseUrl(context));
        var catalog = new CatalogProjection(request, items);
        foreach (var service in snapshot.Index.ServicesById.Values)
        {
            AddService(service, catalog);
        }
        foreach (var dataset in catalog.Datasets.Values)
        {
            items.TryAdd(dataset.Id, dataset);
        }
        return new(ownedIds, items);
    }

    private void AddService(MetadataV2Service service, CatalogProjection catalog)
    {
        var request = catalog.Request;
        var published = projection.ProjectService(request, service);
        if (published is null)
        {
            return;
        }
        catalog.Items.TryAdd(service.Metadata.Id, ProjectService(published, request));
        foreach (var group in published.Publications.GroupBy(publication => publication.ResourceId))
        {
            var publication = group.FirstOrDefault(candidate => candidate.IsPrimary) ?? group.First();
            catalog.Items.TryAdd(publication.Metadata.Id, ProjectLayer(publication, published, request));
            AddDataset(publication, published, catalog);
        }
    }

    private static bool IsDatasetIdentity(MetadataV2Object dataset) => dataset.Type is
        MetadataV2ObjectType.FeatureDataset or MetadataV2ObjectType.Table
        or MetadataV2ObjectType.RasterDataset or MetadataV2ObjectType.TileDataset;

    private void AddDataset(MetadataV2Layer publication, PublishedMetadataService published,
        CatalogProjection catalog)
    {
        var dataset = catalog.Request.Snapshot.ResolveResource(publication)!;
        var kind = dataset.Type switch
        {
            MetadataV2ObjectType.FeatureDataset or MetadataV2ObjectType.Table => "feature-type",
            MetadataV2ObjectType.RasterDataset => "coverage",
            _ => null
        };
        if (kind is null)
        {
            return;
        }
        var visibility = ProjectVisibility(projection.DeriveAccessTier(dataset.AccessPolicy, published.Service.AccessPolicy));
        if (catalog.Datasets.TryGetValue(dataset.Metadata.Id, out var existing) && existing.Visibility >= visibility)
        {
            return;
        }
        catalog.Datasets[dataset.Metadata.Id] = ProjectDataset(dataset, kind, visibility);
    }

    private static ConsoleContentItem ProjectDataset(MetadataV2Object dataset, string kind, ConsoleVisibility visibility)
    {
        var sidecar = new ConsoleDatasetTypeMetadata
        {
            DatasetId = dataset.Metadata.Id,
            Kind = kind,
            FieldCount = dataset.SchemaFields.Count,
            GeometryType = dataset.Spatial?.GeometryType.ToString()
        };
        return ProjectItem(dataset.Metadata, ConsoleContentItemType.Dataset) with
        {
            TypeMetadata = JsonSerializer.SerializeToElement(sidecar, ConsoleJsonContext.Default.ConsoleDatasetTypeMetadata),
            Visibility = visibility,
            Title = dataset.Metadata.Title ?? dataset.Metadata.Name,
            Lifecycle = dataset.Status?.Lifecycle ?? MetadataV2LifecycleStatus.Active,
            OperationalState = dataset.Status?.State ?? MetadataV2OperationalState.Unknown
        };
    }

    private static ConsoleContentItem ProjectService(PublishedMetadataService published, PublishedMetadataRequest request)
    {
        var service = published.Service;
        var sidecar = new ConsoleServiceTypeMetadata
        {
            ServiceType = service.ServiceType,
            RouteBase = Uri.TryCreate(published.Url, UriKind.Absolute, out var serviceUri) ? serviceUri.PathAndQuery : published.Url,
            Publications = published.Publications.Select(publication => publication.PublicationType).Distinct().ToArray(),
            ServiceId = service.Metadata.Id,
            Url = published.Url,
            Access = published.Access,
            FolderPath = MetadataV2ServicePaths.FolderName(request.Snapshot.Graph, service)
        };
        return ProjectItem(service.Metadata, ConsoleContentItemType.Service) with
        {
            TypeMetadata = JsonSerializer.SerializeToElement(sidecar, ConsoleJsonContext.Default.ConsoleServiceTypeMetadata),
            Visibility = ProjectVisibility(published.Access),
            Title = string.IsNullOrWhiteSpace(service.Metadata.Title) ? service.EffectivePublicName : service.Metadata.Title,
            Lifecycle = service.Status?.Lifecycle ?? MetadataV2LifecycleStatus.Active,
            OperationalState = service.Status?.State ?? MetadataV2OperationalState.Unknown
        };
    }

    private ConsoleContentItem ProjectLayer(
        MetadataV2Layer publication, PublishedMetadataService published, PublishedMetadataRequest request)
    {
        var resource = request.Snapshot.ResolveResource(publication)!;
        var accessTier = projection.DeriveAccessTier(resource.AccessPolicy, published.Service.AccessPolicy);
        var sidecar = new ConsoleLayerTypeMetadata
        {
            ServiceId = published.Service.Metadata.Id,
            DatasetId = resource.Metadata.Id,
            FieldCount = resource.SchemaFields.Count,
            GeometryType = resource.Spatial?.GeometryType.ToString(),
            Url = PublishedMetadataProjection.PublicationUrl(request, published.Service, publication),
            Access = accessTier,
            FolderPath = MetadataV2ServicePaths.FolderName(request.Snapshot.Graph, published.Service)
        };
        return ProjectItem(resource.Metadata, ConsoleContentItemType.Layer) with
        {
            Id = publication.Metadata.Id,
            TypeMetadata = JsonSerializer.SerializeToElement(sidecar, ConsoleJsonContext.Default.ConsoleLayerTypeMetadata),
            Visibility = ProjectVisibility(accessTier),
            Title = DescriptiveMetadataResolver.DatasetTitle(resource, publication) ?? resource.Metadata.Name,
            Description = DescriptiveMetadataResolver.DatasetDescription(resource, publication),
            Lifecycle = resource.Status?.Lifecycle ?? MetadataV2LifecycleStatus.Active,
            OperationalState = resource.Status?.State ?? MetadataV2OperationalState.Unknown
        };
    }

    private static ConsoleContentItem ProjectItem(
        MetadataV2ObjectMetadata metadata, ConsoleContentItemType itemType) => new()
        {
            Id = metadata.Id,
            Name = metadata.Name,
            Namespace = metadata.Namespace,
            Description = metadata.Description,
            ItemType = itemType,
            Tags = metadata.Keywords.Count > 0 ? metadata.Keywords : metadata.Tags,
            Labels = metadata.Labels,
            OwnerId = PublishedMetadataProjection.ResolveOwner(metadata),
            CreatedAt = metadata.CreatedAt,
            UpdatedAt = metadata.UpdatedAt ?? metadata.CreatedAt,
            Generation = metadata.Generation,
            IsReadOnly = true
        };

    private sealed record CatalogProjection(
        PublishedMetadataRequest Request, Dictionary<string, ConsoleContentItem> Items)
    {
        public Dictionary<string, ConsoleContentItem> Datasets { get; } = new(StringComparer.Ordinal);
    }

    private static ConsoleVisibility ProjectVisibility(string access) => access switch
    {
        PortalAccessLevels.Public => ConsoleVisibility.Public,
        PortalAccessLevels.Org => ConsoleVisibility.Organization,
        _ => ConsoleVisibility.Personal
    };
}

internal sealed record PublishedConsoleSnapshot(
    IReadOnlySet<string> OwnedIds, IReadOnlyDictionary<string, ConsoleContentItem> Items);

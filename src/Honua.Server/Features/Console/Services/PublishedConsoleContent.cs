// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Services;
using Honua.Core.Features.Portal.Domain;
using Honua.Infrastructure.Helpers;
using Honua.Server.Features.Console.Models;

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
            .Concat(snapshot.Graph.Publications.Select(publication => publication.Metadata.Id)).ToHashSet(StringComparer.Ordinal);
        var items = new Dictionary<string, ConsoleContentItem>(StringComparer.Ordinal);
        var context = contextAccessor.HttpContext;
        if (context is null)
        {
            return new(ownedIds, items);
        }

        var request = new PublishedMetadataRequest(snapshot, context.User, BaseUrlResolver.GetBaseUrl(context));
        foreach (var service in snapshot.Graph.Services)
        {
            var published = projection.ProjectService(request, service);
            if (published is null)
            {
                continue;
            }
            items.Add(service.Metadata.Id, ProjectService(published, request));
            foreach (var publication in published.Publications)
            {
                items.Add(publication.Metadata.Id, ProjectLayer(publication, published, request));
            }
        }
        return new(ownedIds, items);
    }

    private static ConsoleContentItem ProjectService(PublishedMetadataService published, PublishedMetadataRequest request)
    {
        var service = published.Service;
        var sidecar = new PublishedContentMetadata
        {
            ServiceId = service.Metadata.Id,
            Url = published.Url,
            Access = published.Access,
            FolderPath = MetadataV2ServicePaths.FolderName(request.Snapshot.Graph, service)
        };
        return ProjectItem(service.Metadata, ConsoleContentItemType.Service, sidecar) with
        {
            Title = service.Metadata.Title ?? service.EffectivePublicName,
            Lifecycle = service.Status?.Lifecycle ?? MetadataV2LifecycleStatus.Active,
            OperationalState = service.Status?.State ?? MetadataV2OperationalState.Unknown
        };
    }

    private ConsoleContentItem ProjectLayer(
        MetadataV2Publication publication, PublishedMetadataService published, PublishedMetadataRequest request)
    {
        var resource = request.Snapshot.ResolveResource(publication)!;
        var sidecar = new PublishedContentMetadata
        {
            ServiceId = published.Service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            Url = PublishedMetadataProjection.PublicationUrl(request, publication),
            Access = projection.DeriveAccessTier(resource.AccessPolicy, published.Service.AccessPolicy),
            FolderPath = MetadataV2ServicePaths.FolderName(request.Snapshot.Graph, published.Service)
        };
        return ProjectItem(resource.Metadata, ConsoleContentItemType.Layer, sidecar) with
        {
            Id = publication.Metadata.Id,
            Title = publication.TitleOverride ?? resource.Metadata.Title ?? resource.Metadata.Name,
            Lifecycle = resource.Status?.Lifecycle ?? MetadataV2LifecycleStatus.Active,
            OperationalState = resource.Status?.State ?? MetadataV2OperationalState.Unknown
        };
    }

    private static ConsoleContentItem ProjectItem(
        MetadataV2ObjectMetadata metadata, ConsoleContentItemType itemType, PublishedContentMetadata sidecar) => new()
    {
        Id = metadata.Id,
        Name = metadata.Name,
        Namespace = metadata.Namespace,
        Title = metadata.Title ?? metadata.Name,
        Description = metadata.Description,
        ItemType = itemType,
        Tags = metadata.Keywords.Count > 0 ? metadata.Keywords : metadata.Tags,
        Labels = metadata.Labels,
        OwnerId = metadata.Publisher ?? "honua",
        CreatedAt = metadata.CreatedAt,
        UpdatedAt = metadata.UpdatedAt ?? metadata.CreatedAt,
        Generation = metadata.Generation,
        Visibility = sidecar.Access switch
        {
            PortalAccessLevels.Public => ConsoleVisibility.Public,
            PortalAccessLevels.Org => ConsoleVisibility.Organization,
            _ => ConsoleVisibility.Personal
        },
        IsReadOnly = true,
        TypeMetadata = JsonSerializer.SerializeToElement(sidecar, ConsoleJsonContext.Default.PublishedContentMetadata)
    };
}

internal sealed record PublishedConsoleSnapshot(
    IReadOnlySet<string> OwnedIds, IReadOnlyDictionary<string, ConsoleContentItem> Items);

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Db.Postgres.Features.Admin;

internal static class PublishedServiceOrganization
{
    public static MetadataV2Service Apply(MetadataV2Graph graph, MetadataV2Service service, LayerPublishRequest request)
    {
        var existing = graph.Services.FirstOrDefault(s => string.Equals(s.Metadata.Name, service.Metadata.Name, StringComparison.OrdinalIgnoreCase));
        return existing is null ? Create(graph, service, request) : Preserve(existing, service, request);
    }

    private static MetadataV2Service Preserve(MetadataV2Service existing, MetadataV2Service service, LayerPublishRequest request)
    {
        if ((request.FolderId.IsSpecified && request.FolderId.Value != existing.FolderId) ||
            (request.PublicName is not null && request.PublicName != existing.EffectivePublicName))
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict,
                "Publishing cannot change service membership or public name. Use the service move operation for membership changes.");
        return service with { FolderId = existing.FolderId, PublicName = existing.PublicName };
    }

    private static MetadataV2ObjectMetadata PublicDisplayDefaults(MetadataV2ObjectMetadata metadata, string leaf)
        => metadata with
        {
            Title = metadata.Title is null || metadata.Title == metadata.Name ? leaf : metadata.Title,
            Description = metadata.Description is null || metadata.Description == $"Honua service '{metadata.Name}'"
                ? $"Honua service '{leaf}'" : metadata.Description
        };

    private static MetadataV2Service Create(MetadataV2Graph graph, MetadataV2Service service, LayerPublishRequest request)
    {
        var leaf = request.PublicName ?? service.Metadata.Name;
        if (!MetadataV2ServicePaths.IsPublicName(leaf, request.FolderId.Value))
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation, "Invalid or reserved public service name.");
        var folderId = request.FolderId.Value;
        if (folderId is not null && !graph.Folders.Any(f => !f.Deleted && f.Metadata.Id == folderId &&
            MetadataV2ServicePaths.SameTenant(f.Metadata.Tenant, service.Metadata.Tenant)))
            throw new LayerPublishingException(LayerPublishingErrorKind.NotFound, "The folder must exist in the service tenant scope.");
        var published = service with
        {
            FolderId = folderId,
            PublicName = request.PublicName,
            Metadata = PublicDisplayDefaults(service.Metadata, leaf)
        };
        try
        {
            MetadataV2PathReservations.Reconcile(graph, graph with { Services = [.. graph.Services, published] });
        }
        catch (InvalidOperationException)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict, "The public service path is already reserved.");
        }
        return published;
    }
}

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

    private static MetadataV2Service Create(MetadataV2Graph graph, MetadataV2Service service, LayerPublishRequest request)
    {
        var leaf = request.PublicName ?? service.Metadata.Name;
        if (!MetadataV2ServicePaths.IsIdentifier(leaf) || MetadataV2ServicePaths.IsReserved(leaf))
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation, "Invalid or reserved public service name.");
        var folderId = request.FolderId.Value;
        if (folderId is not null && !graph.Folders.Any(f => !f.Deleted && f.Metadata.Id == folderId &&
            MetadataV2ServicePaths.NormalizeTenant(f.Metadata.Tenant) == MetadataV2ServicePaths.NormalizeTenant(service.Metadata.Tenant)))
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict, "The folder must exist in the service tenant scope.");
        var published = service with
        {
            FolderId = folderId, PublicName = request.PublicName,
            Metadata = service.Metadata with { Title = leaf, Description = $"Honua service '{leaf}'" },
            Route = $"/rest/services/{Uri.EscapeDataString(leaf)}/FeatureServer"
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

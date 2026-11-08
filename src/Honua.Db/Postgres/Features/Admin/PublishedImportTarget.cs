// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Db.Postgres.Features.Admin;

internal static class PublishedImportTarget
{
    public static MetadataV2Graph PrepareFolder(MetadataV2Graph graph, LayerPublishRequest request, string? tenant)
    {
        if (request.ImportTarget is not { } target) return graph;
        if (target.ServiceName != request.ServiceName || target.PublicName != request.PublicName ||
            !request.FolderId.IsSpecified || target.FolderId != request.FolderId.Value ||
            target.Namespace != request.Namespace || !MetadataV2ServicePaths.SameTenant(target.TenantId, tenant) ||
            string.IsNullOrWhiteSpace(target.ServiceId) || (target.FolderId is null) != (target.FolderName is null))
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation, "The import publication target does not match its planned identity and scope.");

        return target.PrepareFolder(graph);
    }

    public static MetadataV2Service BindService(MetadataV2Graph graph, MetadataV2Service service, LayerPublishRequest request)
    {
        if (request.ImportTarget is not { } target) return service;
        if (target.ValidateServiceIdentity(graph).Length > 0)
        {
            if (service.Metadata.Id != target.ServiceId)
                throw new LayerPublishingException(LayerPublishingErrorKind.Conflict,
                    "The selected publication service does not match the import plan.");
            return service;
        }
        return service with { Metadata = service.Metadata with { Id = target.ServiceId! } };
    }

    public static void RequireReplacement(MetadataV2Graph graph, string resourceId, LayerPublishRequest request, string? tenant)
    {
        if (request.ImportTarget is not { } target) return;
        var validated = PrepareFolder(graph, request, tenant);
        var services = target.ValidateServiceIdentity(validated).Select(service => service.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        if (services.Count == 0 || !validated.Publications.Any(publication => publication.ResourceId == resourceId &&
            publication.ServiceId is not null && services.Contains(publication.ServiceId)))
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict, "The existing layer does not belong to the planned import service.");
    }
}

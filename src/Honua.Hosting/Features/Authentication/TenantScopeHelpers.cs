// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Authentication;

internal static class TenantScopeHelpers
{
    internal static string? ResolveRequestTenantId(HttpContext context)
        => context.RequestServices?.GetService<ITenantContext>()?.TenantId;

    internal static bool IsTenantVisible(
        HttpContext context,
        MetadataV2Resource? resource,
        MetadataV2Service? service)
    {
        var tenantId = ResolveRequestTenantId(context);
        return MetadataV2TenantVisibility.IsVisibleToTenant(resource, tenantId)
            && MetadataV2TenantVisibility.IsVisibleToTenant(service, tenantId);
    }

    internal static bool IsTenantVisible(
        HttpContext context,
        MetadataV2Publication publication,
        MetadataV2Resource? resource,
        MetadataV2Service? service)
        => MetadataV2TenantVisibility.IsVisibleToTenant(
            publication, resource, service, ResolveRequestTenantId(context));

    internal static IEnumerable<MetadataV2Publication> FilterVisiblePublications(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        IEnumerable<MetadataV2Publication> publications)
        => publications.Where(publication =>
            snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) &&
            snapshot.ResolveResource(publication) is { } resource &&
            IsPublicationVisible(context, publication, resource, service));

    internal static bool IsPublicationVisible(
        HttpContext context,
        MetadataV2Publication publication,
        MetadataV2Resource? resource,
        MetadataV2Service? service)
        => IsTenantVisible(context, publication, resource, service);
}

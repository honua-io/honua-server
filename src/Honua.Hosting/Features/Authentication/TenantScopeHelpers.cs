// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// HTTP-side bridge between the per-request <see cref="ITenantContext"/> rail
/// (issue #1144) and the pure <see cref="MetadataV2TenantVisibility"/> rules
/// (issue #1580). Shared metadata lookup and access-policy helpers consult these
/// methods so tenant isolation is enforced in one place rather than per protocol.
/// </summary>
internal static class TenantScopeHelpers
{
    /// <summary>
    /// Resolves the tenant id of the current request, or <see langword="null"/> when
    /// no tenant context is registered or no tenant was resolved. A null result means
    /// tenant-scoped metadata entities are not visible (fail closed); unscoped
    /// entities remain visible.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <returns>The resolved tenant id, or null.</returns>
    internal static string? ResolveRequestTenantId(HttpContext context)
        => context.RequestServices.GetService<ITenantContext>()?.TenantId;

    /// <summary>
    /// Whether the (resource, service) pair is visible to the current request's tenant.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="resource">The canonical resource, when resolved.</param>
    /// <param name="service">The publishing service, when resolved.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    internal static bool IsTenantVisible(
        HttpContext context,
        MetadataV2Resource? resource,
        MetadataV2Service? service)
    {
        var tenantId = ResolveRequestTenantId(context);
        return MetadataV2TenantVisibility.IsVisibleToTenant(resource, tenantId)
            && MetadataV2TenantVisibility.IsVisibleToTenant(service, tenantId);
    }

    /// <summary>
    /// Whether the published (publication, resource, service) triple is visible to the
    /// current request's tenant.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="publication">The publication being resolved.</param>
    /// <param name="resource">The canonical resource, when resolved.</param>
    /// <param name="service">The publishing service, when resolved.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    internal static bool IsTenantVisible(
        HttpContext context,
        MetadataV2Publication publication,
        MetadataV2Resource? resource,
        MetadataV2Service? service)
        => MetadataV2TenantVisibility.IsVisibleToTenant(
            publication, resource, service, ResolveRequestTenantId(context));

    /// <summary>
    /// Whether a resolved publication/resource/service triple is visible to the current
    /// request tenant.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="publication">The publication being resolved.</param>
    /// <param name="resource">The canonical resource backing the publication, when resolved.</param>
    /// <param name="service">The service the publication is exposed through, when resolved.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    internal static bool IsPublicationVisible(
        HttpContext context,
        MetadataV2Publication publication,
        MetadataV2Resource? resource,
        MetadataV2Service? service)
        => IsTenantVisible(context, publication, resource, service);
}

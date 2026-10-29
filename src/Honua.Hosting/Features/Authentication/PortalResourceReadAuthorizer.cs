// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;
using Honua.Infrastructure.Validation;

namespace Honua.Infrastructure.Authentication;

/// <summary>Connects Portal discovery to canonical service/resource read authorization.</summary>
internal sealed class PortalResourceReadAuthorizer(
    IPortalGroupStore groups,
    IPortalItemSharingStore sharing,
    IHttpContextAccessor contextAccessor,
    IServiceProvider services) : IPortalResourceReadAuthorizer
{
    public bool HasServiceAudience(ClaimsPrincipal principal, MetadataV2Service service)
    {
        if (principal.Identity?.IsAuthenticated != true || !service.IsRoutable())
        {
            return false;
        }

        var username = PortalPrincipal.Resolve(principal);
        var context = contextAccessor.HttpContext;
        if (string.IsNullOrWhiteSpace(username) || context is null || context.User.Identity?.IsAuthenticated != true
            || !string.Equals(username, PortalPrincipal.Resolve(context.User), StringComparison.Ordinal)
            || !TryGetTrustedTenant(context, out var tenantId)
            || !string.Equals(LayerAccessAuthorizer.ResolveAuthorizationTenantId(principal, tenantId), tenantId, StringComparison.Ordinal))
        {
            // Background/principal-only reads cannot borrow an ambient user's membership.
            return false;
        }

        if (context.RequestServices.GetService<IPortalAudienceSnapshotProvider>() is not null)
        {
            // Postgres audiences are loaded asynchronously once after the trusted tenant
            // is known. An absent snapshot denies; never block this synchronous path.
            return PortalAudienceSnapshot.HasService(context, service.Metadata.Id);
        }

        var state = sharing.Get(service.Metadata.Id);
        var membership = groups.GetGroupIdsForUser(username, tenantId);
        // Everyone/org are deliberately unsupported; only exact live group membership grants access.
        return state.GroupIds.Any(id => membership.Contains(id, StringComparer.Ordinal));
    }

    internal static bool TryGetTrustedTenant(HttpContext context, out string? tenantId)
    {
        var tenant = context.RequestServices.GetService<ITenantContext>();
        tenantId = tenant?.TenantId;
        return tenant is not null
            && string.Equals(LayerAccessAuthorizer.ResolveAuthorizationTenantId(context.User, tenantId), tenantId, StringComparison.Ordinal);
    }

    internal static bool IsGroupTenantVisible(HttpContext context, PortalGroupRecord group)
        => TryGetTrustedTenant(context, out var tenantId)
            && string.Equals(group.TenantId, tenantId, StringComparison.Ordinal);

    public AccessDecision Evaluate(
        ClaimsPrincipal principal, MetadataV2Resource? resource, MetadataV2Service service,
        MetadataV2Publication? publication = null)
    {
        var context = contextAccessor.HttpContext;
        var tenantId = context is null ? null : TenantScopeHelpers.ResolveRequestTenantId(context);
        if ((publication is not null && !MetadataV2TenantVisibility.IsVisibleToTenant(publication, resource, service, tenantId))
            || !MetadataV2TenantVisibility.IsVisibleToTenant(resource, tenantId)
            || !MetadataV2TenantVisibility.IsVisibleToTenant(service, tenantId))
        {
            return AccessDecision.Forbidden(AccessPolicyHelpers.TenantScopeDeniedReason);
        }

        return AccessPolicyHelpers.EvaluateScopedKeyAccess(
            principal, service.Metadata.Name, resource?.Metadata.Name, AuthorizationOperation.Query)
            ?? AccessPolicyHelpers.EvaluateServicePolicy(
                context?.RequestServices ?? services, principal, resource?.AccessPolicy, service, AccessScope.Read);
    }
}

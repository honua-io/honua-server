// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Service-scoped RBAC helpers for write operations.
/// </summary>
internal static class ServiceDataEditorAuthorization
{
    public static async Task<IResult?> RequireServiceDataEditorAsync(
        HttpContext context,
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        var decision = await EvaluateServiceAccessAsync(context, serviceId, cancellationToken);
        return CreateDecisionResult(context, decision);
    }

    public static async Task<IResult?> RequireLayerDataEditorAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var decision = await EvaluateLayerAccessAsync(context, layerId, cancellationToken);
        return CreateDecisionResult(context, decision);
    }

    private static Task<AccessDecision> EvaluateServiceAccessAsync(
        HttpContext context,
        string serviceId,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<AccessDecision>(cancellationToken);
        }

        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult(AccessDecision.RequiresAuth("Authentication is required."));
        }

        var options = context.RequestServices.GetRequiredService<IOptions<RbacOptions>>().Value;

        if (IsAdmin(context.User, options))
        {
            return Task.FromResult(AccessDecision.Allowed());
        }

        if (HasGlobalDataEditorRole(context.User, options))
        {
            return Task.FromResult(AccessDecision.Allowed());
        }

        if (HasServiceScopedRole(context.User, options, serviceId))
        {
            return Task.FromResult(AccessDecision.Allowed());
        }

        return Task.FromResult(AccessDecision.Forbidden("User does not have the required data editor role."));
    }

    private static async Task<AccessDecision> EvaluateLayerAccessAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return AccessDecision.RequiresAuth("Authentication is required.");
        }

        var options = context.RequestServices.GetRequiredService<IOptions<RbacOptions>>().Value;

        if (IsAdmin(context.User, options))
        {
            return AccessDecision.Allowed();
        }

        if (HasGlobalDataEditorRole(context.User, options))
        {
            return AccessDecision.Allowed();
        }

        var scopedServiceIds = EnumerateServiceScopedRoleServiceIds(context.User, options)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (scopedServiceIds.Length == 0)
        {
            return AccessDecision.Forbidden("User does not have the required data editor role.");
        }

        var layerCatalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        foreach (var serviceId in scopedServiceIds)
        {
            var service = await layerCatalog.GetServiceAsync(serviceId, cancellationToken);
            if (service?.Layers.Any(layer => layer.Id == layerId) == true)
            {
                return AccessDecision.Allowed();
            }
        }

        return AccessDecision.Forbidden("User does not have the required data editor role.");
    }

    private static IResult? CreateDecisionResult(HttpContext context, AccessDecision decision)
        => AccessPolicyHelpers.CreateAccessDeniedResult(context, decision);

    private static bool IsAdmin(ClaimsPrincipal principal, RbacOptions options)
    {
        foreach (var role in EnumerateRoles(principal, options))
        {
            if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasGlobalDataEditorRole(ClaimsPrincipal principal, RbacOptions options)
    {
        if (options.DataEditorRoles.Length == 0)
        {
            return false;
        }

        foreach (var role in EnumerateRoles(principal, options))
        {
            if (options.DataEditorRoles.Any(allowed =>
                string.Equals(allowed?.Trim(), role, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasServiceScopedRole(ClaimsPrincipal principal, RbacOptions options, string serviceId)
    {
        var prefix = GetServiceScopedRolePrefix(options);

        var expected = string.Concat(prefix, serviceId);

        foreach (var role in EnumerateRoles(principal, options))
        {
            if (string.Equals(role, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateServiceScopedRoleServiceIds(ClaimsPrincipal principal, RbacOptions options)
    {
        var prefix = GetServiceScopedRolePrefix(options);
        foreach (var role in EnumerateRoles(principal, options))
        {
            if (!role.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (role.Length <= prefix.Length)
            {
                continue;
            }

            var serviceId = role[prefix.Length..].Trim();
            if (serviceId.Length > 0)
            {
                yield return serviceId;
            }
        }
    }

    private static string GetServiceScopedRolePrefix(RbacOptions options)
    {
        return string.IsNullOrWhiteSpace(options.DataEditorServicePrefix)
            ? "data-editor:"
            : options.DataEditorServicePrefix.Trim();
    }

    private static IEnumerable<string> EnumerateRoles(ClaimsPrincipal principal, RbacOptions options)
    {
        foreach (var claim in principal.FindAll(ClaimTypes.Role))
        {
            if (!string.IsNullOrWhiteSpace(claim.Value))
            {
                yield return claim.Value;
            }
        }

        var roleClaimType = options.EffectiveRoleClaimType;
        if (!string.Equals(roleClaimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var claim in principal.FindAll(roleClaimType))
            {
                if (!string.IsNullOrWhiteSpace(claim.Value))
                {
                    yield return claim.Value;
                }
            }
        }
    }
}

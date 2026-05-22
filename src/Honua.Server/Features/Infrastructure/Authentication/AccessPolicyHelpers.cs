// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Access policy evaluation helpers for per-layer and per-service authorization.
/// </summary>
internal static class AccessPolicyHelpers
{
    internal const string AuthRequiredMessage = "Authentication is required to access this resource.";
    internal const string AccessForbiddenMessage = "Access to this resource is forbidden.";
    private static readonly ClaimsPrincipal AnonymousPrincipal = new(new ClaimsIdentity());

    /// <summary>
    /// Creates the appropriate error result for a denied access decision.
    /// Returns null if the decision is allowed.
    /// </summary>
    internal static IResult? CreateAccessDeniedResult(HttpContext context, AccessDecision decision)
    {
        if (decision.IsAllowed)
        {
            return null;
        }

        return decision.RequiresAuthentication
            ? StandardErrorHelpers.CreateUnauthorized(context, AuthRequiredMessage)
            : StandardErrorHelpers.CreateForbidden(context, AccessForbiddenMessage);
    }

    public static AccessDecision EvaluateAccess(
        HttpContext context,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy,
        AccessScope scope = AccessScope.Read)
    {
        var evaluator = context.RequestServices.GetRequiredService<IAccessPolicyEvaluator>();

        return evaluator.Evaluate(
            context.User,
            layerPolicy,
            servicePolicy,
            scope);
    }

    public static IResult? RequireAccess(
        HttpContext context,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy,
        AccessScope scope = AccessScope.Read)
    {
        var decision = EvaluateAccess(context, layerPolicy, servicePolicy, scope);
        return CreateAccessDeniedResult(context, decision);
    }

    public static IResult? RequireServiceAccess(
        HttpContext context,
        ServiceDefinition service,
        AccessScope scope = AccessScope.Read)
        => RequireAccess(context, null, service.Metadata?.AccessPolicy, scope);

    public static IResult? RequireLayerAccess(
        HttpContext context,
        LayerDefinition layer,
        ServiceDefinition? service = null,
        AccessScope scope = AccessScope.Read)
        => RequireAccess(context, layer.Metadata?.AccessPolicy, service?.Metadata?.AccessPolicy, scope);

    public static IResult? RequireServiceWriteAccess(HttpContext context, ServiceDefinition service)
        => RequireAccess(context, null, service.Metadata?.AccessPolicy, AccessScope.Write);

    public static IResult? RequireLayerWriteAccess(HttpContext context, LayerDefinition layer, ServiceDefinition? service = null)
        => RequireAccess(context, layer.Metadata?.AccessPolicy, service?.Metadata?.AccessPolicy, AccessScope.Write);

    public static IResult? RequireAnyLayerAccess(
        HttpContext context,
        IEnumerable<LayerDefinition> layers,
        ServiceDefinition? service = null,
        AccessScope scope = AccessScope.Read)
    {
        var requiresAuth = false;
        var hasDenied = false;

        foreach (var layer in layers)
        {
            var decision = EvaluateAccess(context, layer.Metadata?.AccessPolicy, service?.Metadata?.AccessPolicy, scope);
            if (decision.IsAllowed)
            {
                return null;
            }

            hasDenied = true;
            if (decision.RequiresAuthentication)
            {
                requiresAuth = true;
            }
        }

        if (!hasDenied)
        {
            return null;
        }

        return requiresAuth
            ? StandardErrorHelpers.CreateUnauthorized(context, AuthRequiredMessage)
            : StandardErrorHelpers.CreateForbidden(context, AccessForbiddenMessage);
    }

    public static bool IsLayerAccessible(HttpContext context, LayerDefinition layer, ServiceDefinition? service = null)
        => EvaluateAccess(context, layer.Metadata?.AccessPolicy, service?.Metadata?.AccessPolicy).IsAllowed;

    public static bool IsLayerWriteAccessible(HttpContext context, LayerDefinition layer, ServiceDefinition? service = null)
        => EvaluateAccess(context, layer.Metadata?.AccessPolicy, service?.Metadata?.AccessPolicy, AccessScope.Write).IsAllowed;

    public static bool AllowsAnonymousServiceAccess(
        HttpContext context,
        ServiceDefinition service,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(service);

        var evaluator = context.RequestServices.GetRequiredService<IAccessPolicyEvaluator>();
        return evaluator.Evaluate(AnonymousPrincipal, null, service.Metadata?.AccessPolicy, scope).IsAllowed;
    }

    public static bool AllowsAnonymousLayerAccess(
        HttpContext context,
        LayerDefinition layer,
        ServiceDefinition? service = null,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(layer);

        var evaluator = context.RequestServices.GetRequiredService<IAccessPolicyEvaluator>();
        return evaluator.Evaluate(AnonymousPrincipal, layer.Metadata?.AccessPolicy, service?.Metadata?.AccessPolicy, scope).IsAllowed;
    }

    // ---- Metadata v2 overloads. These read the typed AccessPolicy carried on the
    // V2 entities directly, removing the need for consumers to thread v1 LayerDefinition /
    // ServiceDefinition through. They share the underlying RequireAccess / EvaluateAccess
    // implementations so behaviour is identical.

    public static IResult? RequireResourceAccess(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service = null,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return RequireAccess(context, resource.AccessPolicy, service?.AccessPolicy, scope);
    }

    public static IResult? RequireServiceAccess(
        HttpContext context,
        MetadataV2Service service,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(service);
        return RequireAccess(context, null, service.AccessPolicy, scope);
    }

    public static bool IsResourceAccessible(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service = null,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return EvaluateAccess(context, resource.AccessPolicy, service?.AccessPolicy, scope).IsAllowed;
    }

    public static bool AllowsAnonymousResourceAccess(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service = null,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var evaluator = context.RequestServices.GetRequiredService<IAccessPolicyEvaluator>();
        return evaluator.Evaluate(AnonymousPrincipal, resource.AccessPolicy, service?.AccessPolicy, scope).IsAllowed;
    }

    public static bool AllowsAnonymousServiceAccess(
        HttpContext context,
        MetadataV2Service service,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(service);
        var evaluator = context.RequestServices.GetRequiredService<IAccessPolicyEvaluator>();
        return evaluator.Evaluate(AnonymousPrincipal, null, service.AccessPolicy, scope).IsAllowed;
    }

    public static IResult? RequireAnyResourceAccess(
        HttpContext context,
        IEnumerable<MetadataV2Resource> resources,
        MetadataV2Service? service = null,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var requiresAuth = false;
        var hasDenied = false;

        foreach (var resource in resources)
        {
            var decision = EvaluateAccess(context, resource.AccessPolicy, service?.AccessPolicy, scope);
            if (decision.IsAllowed)
            {
                return null;
            }

            hasDenied = true;
            if (decision.RequiresAuthentication)
            {
                requiresAuth = true;
            }
        }

        if (!hasDenied)
        {
            return null;
        }

        return requiresAuth
            ? StandardErrorHelpers.CreateUnauthorized(context, AuthRequiredMessage)
            : StandardErrorHelpers.CreateForbidden(context, AccessForbiddenMessage);
    }
}

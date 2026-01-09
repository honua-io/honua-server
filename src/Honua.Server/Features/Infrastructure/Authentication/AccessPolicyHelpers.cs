// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Access policy evaluation helpers for per-layer and per-service authorization.
/// </summary>
internal static class AccessPolicyHelpers
{
    public static AccessDecision EvaluateAccess(
        HttpContext context,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy)
    {
        var evaluator = context.RequestServices.GetRequiredService<IAccessPolicyEvaluator>();

        return evaluator.Evaluate(
            context.User,
            layerPolicy,
            servicePolicy);
    }

    public static IResult? RequireAccess(
        HttpContext context,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy)
    {
        var decision = EvaluateAccess(context, layerPolicy, servicePolicy);
        if (decision.IsAllowed)
        {
            return null;
        }

        var detail = decision.RequiresAuthentication
            ? "Authentication is required to access this resource."
            : "Access to this resource is forbidden.";

        return decision.RequiresAuthentication
            ? StandardErrorHelpers.CreateUnauthorized(context, detail)
            : StandardErrorHelpers.CreateForbidden(context, detail);
    }

    public static IResult? RequireServiceAccess(HttpContext context, ServiceDefinition service)
        => RequireAccess(context, null, service.Metadata?.AccessPolicy);

    public static IResult? RequireLayerAccess(HttpContext context, LayerDefinition layer, ServiceDefinition? service = null)
        => RequireAccess(context, layer.Metadata?.AccessPolicy, service?.Metadata?.AccessPolicy);

    public static IResult? RequireAnyLayerAccess(
        HttpContext context,
        IEnumerable<LayerDefinition> layers,
        ServiceDefinition? service = null)
    {
        var requiresAuth = false;
        var hasDenied = false;

        foreach (var layer in layers)
        {
            var decision = EvaluateAccess(context, layer.Metadata?.AccessPolicy, service?.Metadata?.AccessPolicy);
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

        var detail = requiresAuth
            ? "Authentication is required to access this resource."
            : "Access to this resource is forbidden.";

        return requiresAuth
            ? StandardErrorHelpers.CreateUnauthorized(context, detail)
            : StandardErrorHelpers.CreateForbidden(context, detail);
    }

    public static bool IsLayerAccessible(HttpContext context, LayerDefinition layer, ServiceDefinition? service = null)
        => EvaluateAccess(context, layer.Metadata?.AccessPolicy, service?.Metadata?.AccessPolicy).IsAllowed;
}

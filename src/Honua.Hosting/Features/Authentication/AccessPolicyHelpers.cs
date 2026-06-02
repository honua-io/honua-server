// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Infrastructure.Authentication;

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

    public static IResult? RequireResourceAccess(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service = null,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return RequireAccess(context, resource.AccessPolicy, service?.AccessPolicy, scope);
    }

    /// <summary>
    /// Resource access check that first consults the canonical per-operation
    /// permission resolver (#1375) over the principal's RBAC grants, then falls
    /// back to the coarse <see cref="AccessPolicy"/> seam when no grant matches.
    /// This is the live wiring of the resolver into an enforced read/write path;
    /// services with no per-operation grants behave exactly as before.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="resource">The resource (layer) being accessed.</param>
    /// <param name="service">The owning service, when known.</param>
    /// <param name="scope">The requested access scope (read/write).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An error result when denied, otherwise <see langword="null"/>.</returns>
    public static async Task<IResult?> RequireResourceAccessAsync(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service = null,
        AccessScope scope = AccessScope.Read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var serviceName = service?.Metadata.Name;
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            var grantDecision = await EvaluateGrantAsync(
                context,
                serviceName,
                resource.Metadata.Name,
                scope,
                cancellationToken).ConfigureAwait(false);

            // An explicit per-operation grant authorizes the request directly.
            if (grantDecision == GrantOutcome.Allow)
            {
                return null;
            }
        }

        // No matching grant (or no service context): preserve current behavior
        // by falling back to the coarse AccessPolicy evaluation.
        return RequireAccess(context, resource.AccessPolicy, service?.AccessPolicy, scope);
    }

    /// <summary>
    /// Consults the per-operation permission resolver for the supplied
    /// <c>(service, layer, operation)</c> tuple, mapping the request principal's
    /// claims to roles. Returns whether an explicit grant allows the request.
    /// </summary>
    private static async Task<GrantOutcome> EvaluateGrantAsync(
        HttpContext context,
        string serviceName,
        string? layerName,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var resolver = context.RequestServices.GetService<IPermissionResolver>();
        if (resolver is null)
        {
            return GrantOutcome.NoGrant;
        }

        var principal = context.User;
        var options = context.RequestServices.GetRequiredService<IOptions<RbacOptions>>().Value;
        var roles = EnumeratePrincipalRoles(principal, options);
        if (roles.Count == 0)
        {
            // No roles to resolve grants from — defer to the coarse policy.
            return GrantOutcome.NoGrant;
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? string.Empty;
        var isAuthenticated = principal.Identity?.IsAuthenticated == true;

        var operation = scope == AccessScope.Write
            ? AuthorizationOperation.Update
            : AuthorizationOperation.Query;

        var decision = await resolver.AuthorizeAsync(
            userId,
            roles,
            serviceName,
            layerName,
            operation,
            isAuthenticated,
            cancellationToken).ConfigureAwait(false);

        return decision.IsAllowed ? GrantOutcome.Allow : GrantOutcome.NoGrant;
    }

    private static List<string> EnumeratePrincipalRoles(ClaimsPrincipal principal, RbacOptions options)
    {
        var roles = new List<string>();

        foreach (var claim in principal.FindAll(ClaimTypes.Role))
        {
            if (!string.IsNullOrWhiteSpace(claim.Value))
            {
                roles.Add(claim.Value);
            }
        }

        var roleClaimType = options.EffectiveRoleClaimType;
        if (!string.Equals(roleClaimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var claim in principal.FindAll(roleClaimType))
            {
                if (!string.IsNullOrWhiteSpace(claim.Value))
                {
                    roles.Add(claim.Value);
                }
            }
        }

        return roles;
    }

    private enum GrantOutcome
    {
        NoGrant = 0,
        Allow = 1,
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

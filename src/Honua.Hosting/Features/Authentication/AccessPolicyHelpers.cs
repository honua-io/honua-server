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
    public static Task<IResult?> RequireResourceAccessAsync(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service = null,
        AccessScope scope = AccessScope.Read,
        CancellationToken cancellationToken = default)
        => RequireResourceAccessAsync(
            context,
            resource,
            DefaultOperationForScope(scope),
            service,
            cancellationToken);

    /// <summary>
    /// Operation-aware resource access check (#1376). Routes the request through
    /// the canonical per-operation permission resolver for the supplied
    /// <see cref="AuthorizationOperation"/>, then falls back to the coarse
    /// <see cref="AccessPolicy"/> seam (using the scope implied by the operation)
    /// when no grant matches. This is the shared seam every protocol adapter
    /// re-wires to so a per-operation grant (e.g. allow <c>query</c> but deny
    /// <c>update</c>) is honored consistently across surfaces while services with
    /// no per-operation grants keep their current coarse behavior.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="resource">The resource (layer) being accessed.</param>
    /// <param name="operation">The canonical operation being authorized.</param>
    /// <param name="service">The owning service, when known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An error result when denied, otherwise <see langword="null"/>.</returns>
    public static async Task<IResult?> RequireResourceAccessAsync(
        HttpContext context,
        MetadataV2Resource resource,
        AuthorizationOperation operation,
        MetadataV2Service? service = null,
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
                operation,
                cancellationToken).ConfigureAwait(false);

            // An explicit per-operation grant authorizes the request directly.
            if (grantDecision == GrantOutcome.Allow)
            {
                return null;
            }
        }

        // No matching grant (or no service context): preserve current behavior
        // by falling back to the coarse AccessPolicy evaluation.
        return RequireAccess(context, resource.AccessPolicy, service?.AccessPolicy, ScopeForOperation(operation));
    }

    /// <summary>
    /// Service-level operation-aware access check (#1376). Mirrors
    /// <see cref="RequireResourceAccessAsync(HttpContext, MetadataV2Resource, AuthorizationOperation, MetadataV2Service?, CancellationToken)"/>
    /// for service-scoped operations (no specific layer), consulting the resolver
    /// with a wildcard layer then falling back to the coarse service policy.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="service">The service being accessed.</param>
    /// <param name="operation">The canonical operation being authorized.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An error result when denied, otherwise <see langword="null"/>.</returns>
    public static async Task<IResult?> RequireServiceAccessAsync(
        HttpContext context,
        MetadataV2Service service,
        AuthorizationOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);

        var serviceName = service.Metadata.Name;
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            var grantDecision = await EvaluateGrantAsync(
                context,
                serviceName,
                layerName: null,
                operation,
                cancellationToken).ConfigureAwait(false);

            if (grantDecision == GrantOutcome.Allow)
            {
                return null;
            }
        }

        return RequireAccess(context, null, service.AccessPolicy, ScopeForOperation(operation));
    }

    /// <summary>
    /// Operation-aware resource access evaluation that returns an
    /// <see cref="AccessDecision"/> (rather than an <see cref="IResult"/>) for
    /// non-HTTP adapters such as gRPC (#1376). Consults the resolver first and
    /// reports the matched grant as an allowed decision; otherwise it falls back
    /// to the coarse <see cref="AccessPolicy"/> evaluation for the implied scope.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="resource">The resource (layer) being accessed.</param>
    /// <param name="service">The owning service, when known.</param>
    /// <param name="operation">The canonical operation being authorized.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The access decision.</returns>
    public static async Task<AccessDecision> EvaluateResourceAccessAsync(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service,
        AuthorizationOperation operation,
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
                operation,
                cancellationToken).ConfigureAwait(false);

            if (grantDecision == GrantOutcome.Allow)
            {
                return AccessDecision.Allowed();
            }
        }

        return EvaluateAccess(context, resource.AccessPolicy, service?.AccessPolicy, ScopeForOperation(operation));
    }

    /// <summary>
    /// Maps an <see cref="AccessScope"/> to the canonical read/write operation it
    /// implies. Read scope maps to <see cref="AuthorizationOperation.Query"/> and
    /// write scope to <see cref="AuthorizationOperation.Update"/>; callers that
    /// distinguish insert/delete/export/metadata should use the operation-aware
    /// overloads directly.
    /// </summary>
    /// <param name="scope">The coarse access scope.</param>
    /// <returns>The implied canonical operation.</returns>
    public static AuthorizationOperation DefaultOperationForScope(AccessScope scope)
        => scope == AccessScope.Write
            ? AuthorizationOperation.Update
            : AuthorizationOperation.Query;

    /// <summary>
    /// Maps a canonical operation back to the coarse <see cref="AccessScope"/>
    /// used by the legacy <see cref="AccessPolicy"/> fallback. Mutating operations
    /// (insert/update/delete/admin) require write scope; read-style operations
    /// (query/read/metadata/export) require read scope.
    /// </summary>
    private static AccessScope ScopeForOperation(AuthorizationOperation operation) => operation switch
    {
        AuthorizationOperation.Insert => AccessScope.Write,
        AuthorizationOperation.Update => AccessScope.Write,
        AuthorizationOperation.Delete => AccessScope.Write,
        AuthorizationOperation.Admin => AccessScope.Write,
        _ => AccessScope.Read,
    };

    /// <summary>
    /// Consults the per-operation permission resolver for the supplied
    /// <c>(service, layer, operation)</c> tuple, mapping the request principal's
    /// claims to roles. Returns whether an explicit grant allows the request.
    /// </summary>
    private static async Task<GrantOutcome> EvaluateGrantAsync(
        HttpContext context,
        string serviceName,
        string? layerName,
        AuthorizationOperation operation,
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

    /// <summary>
    /// Resolver-aware accessibility predicate (#1376). Consults the canonical
    /// per-operation permission resolver for the supplied operation first; when no
    /// grant matches it falls back to the coarse <see cref="AccessPolicy"/>
    /// evaluation, so visibility filtering (e.g. WFS GetFeature published types)
    /// honors per-operation grants while ungranted services behave as before.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="resource">The resource (layer) being accessed.</param>
    /// <param name="service">The owning service, when known.</param>
    /// <param name="operation">The canonical operation being authorized.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the resource is accessible.</returns>
    public static async Task<bool> IsResourceAccessibleAsync(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service,
        AuthorizationOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var decision = await EvaluateResourceAccessAsync(
            context, resource, service, operation, cancellationToken).ConfigureAwait(false);
        return decision.IsAllowed;
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

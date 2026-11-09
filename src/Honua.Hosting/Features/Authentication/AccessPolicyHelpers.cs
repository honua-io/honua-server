// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using System.Security.Claims;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Infrastructure.Authentication;

internal static class AccessPolicyHelpers
{
    internal const string AuthRequiredMessage = "Authentication is required to access this resource.";
    internal const string AccessForbiddenMessage = "Access to this resource is forbidden.";

    // The hidden-metadata denial stays internal so a forbidden response cannot reveal which tenant owns the resource.
    internal const string TenantScopeDeniedReason = "Resource is outside the request tenant scope.";

    private static readonly ClaimsPrincipal AnonymousPrincipal = new(new ClaimsIdentity());
    private static readonly object EffectivePermissionsTaskKey = new();

    internal static IResult? CreateAccessDeniedResult(HttpContext context, AccessDecision decision)
    {
        if (decision.IsAllowed)
        {
            return null;
        }

        if (decision.RequiresAuthentication)
        {
            AppendAuthenticationChallenge(context);
            return StandardErrorHelpers.CreateUnauthorized(context, AuthRequiredMessage);
        }

        return StandardErrorHelpers.CreateForbidden(context, AccessForbiddenMessage);
    }

    internal static void AppendAuthenticationChallenge(HttpContext context)
    {
        context.Response.Headers.Append(
            "WWW-Authenticate",
            AttemptedBearerCredential(context.Request)
                ? "Bearer"
                : "ApiKey realm=\"Honua Admin\", header=\"X-API-Key\"");
    }

    // Portal tokens travel as an Authorization or X-Esri-Authorization bearer, the `token` query
    // parameter, or a form field the portal-token handler has already parsed. A caller that tried
    // any of them is told to present a bearer again, not an admin API key (honua-server#4778).
    private static bool AttemptedBearerCredential(HttpRequest request)
    {
        static bool IsBearer(StringValues values) => values.Any(value => value is not null
            && value.StartsWith(PortalTokenAuthenticationHandler.BearerPrefix, StringComparison.OrdinalIgnoreCase));

        return IsBearer(request.Headers.Authorization)
            || IsBearer(request.Headers[PortalTokenAuthenticationHandler.GeoServicesAuthorizationHeader])
            || request.Query.ContainsKey(PortalTokenAuthenticationHandler.TokenQueryParameter)
            || request.HttpContext.Features.Get<IFormFeature>()?.Form?.ContainsKey(PortalTokenAuthenticationHandler.TokenQueryParameter) == true;
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
        MetadataV2Object resource,
        MetadataV2Service? service = null,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!TenantScopeHelpers.IsTenantVisible(context, resource, service))
        {
            return StandardErrorHelpers.CreateForbidden(context, AccessForbiddenMessage);
        }

        if (scope == AccessScope.Write && MetadataV2RelationshipEditPolicy.RequiresReadOnly(resource))
        {
            return StandardErrorHelpers.CreateForbidden(context, MetadataV2RelationshipEditPolicy.ReadOnlyReason);
        }

        return CreateAccessDeniedResult(context, EvaluateResourceAccess(context, resource, service, scope));
    }

    public static Task<IResult?> RequireResourceAccessAsync(
        HttpContext context,
        MetadataV2Object resource,
        MetadataV2Service? service = null,
        AccessScope scope = AccessScope.Read,
        CancellationToken cancellationToken = default)
        => RequireResourceAccessAsync(
            context,
            resource,
            DefaultOperationForScope(scope),
            service,
            cancellationToken);

    public static async Task<IResult?> RequireResourceAccessAsync(
        HttpContext context,
        MetadataV2Object resource,
        AuthorizationOperation operation,
        MetadataV2Service? service = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var decision = await EvaluateResourceAccessCoreAsync(
            context.RequestServices,
            context.User,
            TenantScopeHelpers.ResolveRequestTenantId(context),
            applyTenantScope: true,
            resource,
            service,
            operation,
            cancellationToken,
            requestCache: context.Items).ConfigureAwait(false);

        return CreateAccessDeniedResult(context, decision);
    }

    public static async Task<IResult?> RequireServiceAccessAsync(
        HttpContext context,
        MetadataV2Service service,
        AuthorizationOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (!TenantScopeHelpers.IsTenantVisible(context, resource: null, service))
        {
            return StandardErrorHelpers.CreateForbidden(context, AccessForbiddenMessage);
        }

        var serviceName = service.Metadata.Name;
        var scopedKeyDecision = EvaluateScopedKeyAccess(context.User, serviceName, null, operation);
        if (scopedKeyDecision is { } decision)
        {
            return CreateAccessDeniedResult(context, decision);
        }

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            var grantDecision = await EvaluateGrantAsync(
                context,
                serviceName,
                layerName: null,
                operation,
                cancellationToken).ConfigureAwait(false);

            if (grantDecision.IsAllowed)
            {
                if (!NarrowsWithRoleList(grantDecision.MatchedGrant))
                {
                    return null;
                }

                var granted = ApplyRoleListToGrant(
                    context.RequestServices, context.User, resourcePolicy: null, service, operation);
                return granted.IsAllowed ? null : CreateAccessDeniedResult(context, granted);
            }
        }

        return CreateAccessDeniedResult(context, EvaluateServicePolicy(
            context.RequestServices, context.User, null, service, ScopeForOperation(operation), operation));
    }

    public static Task<AccessDecision> EvaluateResourceAccessAsync(
        HttpContext context,
        MetadataV2Object resource,
        MetadataV2Service? service,
        AuthorizationOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return EvaluateResourceAccessCoreAsync(
            context.RequestServices,
            context.User,
            TenantScopeHelpers.ResolveRequestTenantId(context),
            applyTenantScope: true,
            resource,
            service,
            operation,
            cancellationToken,
            requestCache: context.Items);
    }

    // HTTP and geoprocessing submit share this decision. applyTenantScope false is the tenantless exception; leaving it true with no tenant denies every tenant-scoped resource.
    internal static async Task<AccessDecision> EvaluateResourceAccessCoreAsync(
        IServiceProvider services,
        ClaimsPrincipal principal,
        string? tenantId,
        bool applyTenantScope,
        MetadataV2Object resource,
        MetadataV2Service? service,
        AuthorizationOperation operation,
        CancellationToken cancellationToken,
        IDictionary<object, object?>? requestCache = null)
    {
        ArgumentNullException.ThrowIfNull(resource);

        // Tenant scope gates before grant evaluation (#1580): a per-operation grant
        // must never authorize access to another tenant's resource.
        if (applyTenantScope &&
            !(MetadataV2TenantVisibility.IsVisibleToTenant(resource, tenantId)
                && MetadataV2TenantVisibility.IsVisibleToTenant(service, tenantId)))
        {
            return AccessDecision.Forbidden(TenantScopeDeniedReason);
        }

        if (operation is AuthorizationOperation.Insert or AuthorizationOperation.Update or AuthorizationOperation.Delete &&
            MetadataV2RelationshipEditPolicy.RequiresReadOnly(resource))
        {
            return AccessDecision.Forbidden(MetadataV2RelationshipEditPolicy.ReadOnlyReason);
        }

        var serviceName = service?.Metadata.Name;
        var scopedKeyDecision = EvaluateScopedKeyAccess(principal, serviceName, resource.Metadata.Name, operation);
        if (scopedKeyDecision is { } decision)
        {
            return decision;
        }

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            var grantDecision = await EvaluateGrantCoreAsync(
                services,
                principal,
                serviceName,
                resource.Metadata.Name,
                operation,
                cancellationToken,
                requestCache).ConfigureAwait(false);

            // A built-in wildcard grant does not skip an explicit role list (#6358).
            // A grant that names this service and layer is an explicit allow and stands.
            if (grantDecision.IsAllowed)
            {
                return NarrowsWithRoleList(grantDecision.MatchedGrant)
                    ? ApplyRoleListToGrant(services, principal, resource.AccessPolicy, service, operation)
                    : AccessDecision.Allowed();
            }
        }

        // No matching grant (or no service context): preserve current behavior
        // by falling back to the coarse AccessPolicy evaluation.
        return EvaluateServicePolicy(
            services, principal, resource.AccessPolicy, service, ScopeForOperation(operation), operation);
    }

    public static AuthorizationOperation DefaultOperationForScope(AccessScope scope)
        => scope == AccessScope.Write
            ? AuthorizationOperation.Update
            : AuthorizationOperation.Query;

    private static AccessScope ScopeForOperation(AuthorizationOperation operation) => operation switch
    {
        AuthorizationOperation.Insert => AccessScope.Write,
        AuthorizationOperation.Update => AccessScope.Write,
        AuthorizationOperation.Delete => AccessScope.Write,
        AuthorizationOperation.Admin => AccessScope.Write,
        _ => AccessScope.Read,
    };

    // Scoped API-key grants must be checked against the target before any coarse
    // policy or role grant can authorize the request. A missing target fails closed.
    internal static AccessDecision? EvaluateScopedKeyAccess(
        ClaimsPrincipal principal, string? serviceName, string? layerName, AuthorizationOperation operation)
    {
        if (!LayerScopedWriteKey.IsScopeGovernedPrincipal(principal))
        {
            return null;
        }

        var allowed = operation is AuthorizationOperation.Query or AuthorizationOperation.Read
            or AuthorizationOperation.Metadata or AuthorizationOperation.Export
            ? LayerScopedWriteKey.AllowsRead(principal, serviceName, layerName)
            : operation is AuthorizationOperation.Insert or AuthorizationOperation.Update or AuthorizationOperation.Delete
              && LayerScopedWriteKey.IsScopedWritePrincipal(principal)
              && LayerScopedWriteKey.AllowsWrite(principal, serviceName, layerName);

        return allowed
            ? AccessDecision.Allowed()
            : AccessDecision.Forbidden("API key permission does not grant access to this resource.");
    }

    private static Task<GrantEvaluation> EvaluateGrantAsync(
        HttpContext context,
        string serviceName,
        string? layerName,
        AuthorizationOperation operation,
        CancellationToken cancellationToken)
        => EvaluateGrantCoreAsync(
            context.RequestServices,
            context.User,
            serviceName,
            layerName,
            operation,
            cancellationToken,
            context.Items);

    private static async Task<GrantEvaluation> EvaluateGrantCoreAsync(
        IServiceProvider services,
        ClaimsPrincipal principal,
        string serviceName,
        string? layerName,
        AuthorizationOperation operation,
        CancellationToken cancellationToken,
        IDictionary<object, object?>? requestCache = null)
    {
        var resolver = services.GetService<IPermissionResolver>();
        if (resolver is null)
        {
            return GrantEvaluation.None;
        }

        var options = services.GetRequiredService<IOptions<RbacOptions>>().Value;
        var roles = RbacRoleClaims.Enumerate(principal, options, services);
        if (roles.Count == 0)
        {
            // No roles to resolve grants from — defer to the coarse policy.
            return GrantEvaluation.None;
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? string.Empty;
        var isAuthenticated = principal.Identity?.IsAuthenticated == true;

        PermissionDecision decision;
        var roleStore = services.GetService<IRoleStore>();
        if (resolver is PermissionResolver && roleStore is not null)
        {
            // Catalog/list endpoints can evaluate many resources or services in one request.
            // Resolve the caller-wide effective grants once, then apply the resolver's local
            // operation matching for each item instead of repeating the same role-store query.
            Task<EffectivePermissions> effectiveTask;
            if (requestCache is not null
                && requestCache.TryGetValue(EffectivePermissionsTaskKey, out var cached)
                && cached is Task<EffectivePermissions> cachedTask)
            {
                effectiveTask = cachedTask;
            }
            else
            {
                effectiveTask = roleStore.GetEffectivePermissionsAsync(userId, roles, cancellationToken);
                if (requestCache is not null)
                {
                    requestCache[EffectivePermissionsTaskKey] = effectiveTask;
                }
            }

            var effectivePermissions = await effectiveTask.ConfigureAwait(false);
            decision = resolver.Authorize(effectivePermissions, serviceName, layerName, operation);
        }
        else
        {
            // Preserve custom resolver behavior, including registrations that also expose an
            // IRoleStore but implement additional logic in AuthorizeAsync.
            decision = await resolver.AuthorizeAsync(
                userId,
                roles,
                serviceName,
                layerName,
                operation,
                isAuthenticated,
                cancellationToken).ConfigureAwait(false);
        }

        return decision.IsAllowed
            ? GrantEvaluation.Allowed(decision.MatchedGrant)
            : GrantEvaluation.None;
    }

    private readonly record struct GrantEvaluation(bool IsAllowed, PermissionGrant? MatchedGrant)
    {
        public static GrantEvaluation None => default;

        public static GrantEvaluation Allowed(PermissionGrant? grant) => new(true, grant);
    }

    // A broad built-in viewer or editor grant still has to satisfy the layer role list; a grant that names both service and layer is an explicit allow.
    private static bool NarrowsWithRoleList(PermissionGrant? grant)
        => grant is null
            || string.Equals(grant.Service, "*", StringComparison.Ordinal)
            || string.Equals(grant.Layer, "*", StringComparison.Ordinal);

    public static IResult? RequireServiceAccess(
        HttpContext context,
        MetadataV2Service service,
        AccessScope scope = AccessScope.Read)
        => CreateAccessDeniedResult(context, EvaluateServiceAccess(context, service, scope));

    public static AccessDecision EvaluateServiceAccess(
        HttpContext context,
        MetadataV2Service service,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!TenantScopeHelpers.IsTenantVisible(context, resource: null, service))
        {
            return AccessDecision.Forbidden(AccessForbiddenMessage);
        }

        return EvaluateScopedKeyAccess(context.User, service.Metadata.Name, null, DefaultOperationForScope(scope))
            ?? EvaluateServicePolicy(context.RequestServices, context.User, null, service, scope);
    }

    public static bool IsResourceAccessible(
        HttpContext context,
        MetadataV2Object resource,
        MetadataV2Service? service = null,
        AccessScope scope = AccessScope.Read)
        => EvaluateResourceAccess(context, resource, service, scope).IsAllowed;

    // A foreign-tenant resource is forbidden before the coarse seam so list surfaces hide it the same way an item check does.
    public static AccessDecision EvaluateResourceAccess(
        HttpContext context,
        MetadataV2Object resource,
        MetadataV2Service? service = null,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!TenantScopeHelpers.IsTenantVisible(context, resource, service))
        {
            return AccessDecision.Forbidden(TenantScopeDeniedReason);
        }

        if (scope == AccessScope.Write && MetadataV2RelationshipEditPolicy.RequiresReadOnly(resource))
        {
            return AccessDecision.Forbidden(MetadataV2RelationshipEditPolicy.ReadOnlyReason);
        }

        return EvaluateScopedKeyAccess(context.User, service?.Metadata.Name, resource.Metadata.Name, DefaultOperationForScope(scope))
            ?? EvaluateServicePolicy(context.RequestServices, context.User, resource.AccessPolicy, service, scope);
    }

    // An explicit group audience is an alternative service READ grant only.
    // Resource policy remains authoritative, and callers enforce tenant/key scope first.
    internal static AccessDecision EvaluateServicePolicy(
        IServiceProvider services,
        ClaimsPrincipal principal,
        AccessPolicy? resourcePolicy,
        MetadataV2Service? service,
        AccessScope scope,
        AuthorizationOperation operation = AuthorizationOperation.Query)
    {
        var servicePolicy = service?.AccessPolicy;
        if (scope == AccessScope.Read
            && (operation is AuthorizationOperation.Query or AuthorizationOperation.Read
                or AuthorizationOperation.Metadata or AuthorizationOperation.Export)
            && service is not null
            && services.GetService<IPortalResourceReadAuthorizer>()?.HasServiceAudience(principal, service) == true)
        {
            servicePolicy = null;
        }

        return services.GetRequiredService<IAccessPolicyEvaluator>()
            .Evaluate(principal, resourcePolicy, servicePolicy, scope);
    }

    // Empty role lists and AllowAnonymous still allow, so the grant stands. A forbidden
    // decision stands even when a replacement evaluator uses its own failure text.
    // Authentication-required is not a role-list denial.
    private static AccessDecision ApplyRoleListToGrant(
        IServiceProvider services,
        ClaimsPrincipal principal,
        AccessPolicy? resourcePolicy,
        MetadataV2Service? service,
        AuthorizationOperation operation)
    {
        var policyDecision = EvaluateServicePolicy(
            services,
            principal,
            resourcePolicy,
            service,
            ScopeForOperation(operation),
            operation);
        return policyDecision is { IsAllowed: false, RequiresAuthentication: false }
            ? policyDecision
            : AccessDecision.Allowed();
    }

    public static async Task<bool> IsResourceAccessibleAsync(
        HttpContext context,
        MetadataV2Object resource,
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
        MetadataV2Object resource,
        MetadataV2Service? service = null,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!TenantScopeHelpers.IsTenantVisible(context, resource, service))
        {
            return false;
        }

        var evaluator = context.RequestServices.GetRequiredService<IAccessPolicyEvaluator>();
        return evaluator.Evaluate(AnonymousPrincipal, resource.AccessPolicy, service?.AccessPolicy, scope).IsAllowed;
    }

    public static bool AllowsAnonymousServiceAccess(
        HttpContext context,
        MetadataV2Service service,
        AccessScope scope = AccessScope.Read)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!TenantScopeHelpers.IsTenantVisible(context, resource: null, service))
        {
            return false;
        }

        var evaluator = context.RequestServices.GetRequiredService<IAccessPolicyEvaluator>();
        return evaluator.Evaluate(AnonymousPrincipal, null, service.AccessPolicy, scope).IsAllowed;
    }

    public static async Task<IResult?> RequireAnyMutatingOperationAccessAsync(
        HttpContext context,
        MetadataV2Object resource,
        MetadataV2Service? service = null,
        CancellationToken cancellationToken = default)
    {
        // Allow the request if any one of the three write operations is permitted.
        var updateError = await RequireResourceAccessAsync(context, resource, AuthorizationOperation.Update, service, cancellationToken).ConfigureAwait(false);
        if (updateError is null)
        {
            return null;
        }

        var insertError = await RequireResourceAccessAsync(context, resource, AuthorizationOperation.Insert, service, cancellationToken).ConfigureAwait(false);
        if (insertError is null)
        {
            return null;
        }

        var deleteError = await RequireResourceAccessAsync(context, resource, AuthorizationOperation.Delete, service, cancellationToken).ConfigureAwait(false);
        if (deleteError is null)
        {
            return null;
        }

        // All three denied — surface the Update denial as the representative error
        // (matches the caller's expectation for a service with no per-operation grants).
        return updateError;
    }

    public static async Task<(T[] Resources, IResult? Error)> FilterAccessibleResourcesAsync<T>(
        HttpContext context,
        IEnumerable<T> candidates,
        Func<T, MetadataV2Object> resourceSelector,
        MetadataV2Service service,
        AuthorizationOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(resourceSelector);
        var visible = new List<T>();
        AccessDecision? denial = null;
        foreach (var candidate in candidates)
        {
            var decision = await EvaluateResourceAccessAsync(
                context, resourceSelector(candidate), service, operation, cancellationToken).ConfigureAwait(false);
            if (decision.IsAllowed)
            {
                visible.Add(candidate);
            }
            else if (denial is null || decision.RequiresAuthentication)
            {
                denial = decision;
            }
        }

        return (visible.ToArray(), visible.Count == 0 && denial is not null
            ? CreateAccessDeniedResult(context, denial.Value)
            : null);
    }

    public static Task<ResourceAccessSet> EvaluateResourceAccessSetAsync(
        HttpContext context,
        IEnumerable<MetadataV2Object> resources,
        MetadataV2Service? service,
        AuthorizationOperation operation,
        CancellationToken cancellationToken = default)
        => EvaluateResourceAccessSetAsync(context, resources, service, [operation], cancellationToken);

    public static async Task<ResourceAccessSet> EvaluateResourceAccessSetAsync(
        HttpContext context,
        IEnumerable<MetadataV2Object> resources,
        MetadataV2Service? service,
        IReadOnlyList<AuthorizationOperation> operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
        {
            throw new ArgumentException("At least one operation is required.", nameof(operations));
        }

        var decisions = new Dictionary<MetadataV2Object, AccessDecision>(ReferenceEqualityComparer.Instance);
        foreach (var resource in resources.Distinct<MetadataV2Object>(ReferenceEqualityComparer.Instance))
        {
            AccessDecision? decision = null;
            foreach (var operation in operations)
            {
                var candidate = await EvaluateResourceAccessAsync(
                    context, resource, service, operation, cancellationToken).ConfigureAwait(false);
                if (candidate.IsAllowed)
                {
                    decision = candidate;
                    break;
                }

                decision ??= candidate;
            }

            decisions[resource] = decision!.Value;
        }

        return new ResourceAccessSet(context, service, ScopeForOperation(operations[0]), decisions);
    }

    public static async Task<IResult?> RequireAnyResourceAccessAsync(
        HttpContext context,
        IEnumerable<MetadataV2Object> resources,
        MetadataV2Service? service,
        AuthorizationOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        IReadOnlyCollection<MetadataV2Object> candidates = resources as IReadOnlyCollection<MetadataV2Object> ?? [.. resources];
        var access = await EvaluateResourceAccessSetAsync(
            context, candidates, service, operation, cancellationToken).ConfigureAwait(false);
        return access.RequireAny(candidates);
    }

    public static IResult? RequireAnyResourceAccess(
        HttpContext context,
        IEnumerable<MetadataV2Object> resources,
        MetadataV2Service? service = null,
        AccessScope scope = AccessScope.Read)
        => RequireAnyDecision(
            context,
            resources,
            resource => EvaluateResourceAccess(context, resource, service, scope));

    internal static IResult? RequireAnyDecision(
        HttpContext context,
        IEnumerable<MetadataV2Object> resources,
        Func<MetadataV2Object, AccessDecision> evaluate)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var requiresAuth = false;
        var hasDenied = false;

        // codeql[cs/linq/missed-select]: returns on the first allow and accumulates denial flags across the rest
        foreach (var resource in resources)
        {
            var decision = evaluate(resource);
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

        if (requiresAuth)
        {
            AppendAuthenticationChallenge(context);
            return StandardErrorHelpers.CreateUnauthorized(context, AuthRequiredMessage);
        }

        return StandardErrorHelpers.CreateForbidden(context, AccessForbiddenMessage);
    }
}

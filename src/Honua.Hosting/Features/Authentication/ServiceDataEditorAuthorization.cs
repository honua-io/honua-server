// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Infrastructure.Authentication;

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

    public static async Task<IResult?> RequireServiceDataEditorAsync(
        HttpContext context,
        MetadataV2Service service,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);

        // Layer-scoped write keys (#1637) are enforced here in the shared pipeline:
        // a scoped key authorizes a write only when one of its grants matches the
        // target service; otherwise the write is forbidden. Scoped keys never fall
        // through to the admin/data-editor role checks below.
        if (LayerScopedWriteKey.IsScopedWritePrincipal(context.User))
        {
            return EvaluateScopedWriteKey(context, service.Metadata.Name, layerName: null);
        }

        // An explicit AccessPolicy write restriction stays authoritative (it is a
        // deliberate per-resource grant/deny and must not be widened by a wildcard
        // RBAC grant). When no explicit write policy applies, a per-operation RBAC
        // write grant (#1376) authorizes the mutation, bypassing the coarse
        // data-editor role gate; absent any grant, behavior is unchanged.
        var explicitDecision = EvaluateExplicitWritePolicy(context, null, service.AccessPolicy);
        if (explicitDecision is not null)
        {
            return CreateDecisionResult(context, explicitDecision.Value);
        }

        if (await HasWriteGrantAsync(context, service.Metadata.Name, layerName: null, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var decision = await EvaluateServiceAccessAsync(context, service.Metadata.Name, cancellationToken).ConfigureAwait(false);
        return CreateDecisionResult(context, decision);
    }

    public static async Task<IResult?> RequireResourceDataEditorAsync(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service = null,
        CancellationToken cancellationToken = default)
    {
        return await RequireResourceDataEditorCoreAsync(context, resource, service, specificOperation: null, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IResult?> RequireResourceDataEditorAsync(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service,
        AuthorizationOperation operation,
        CancellationToken cancellationToken = default)
    {
        return await RequireResourceDataEditorCoreAsync(context, resource, service, operation, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult?> RequireResourceDataEditorCoreAsync(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service,
        AuthorizationOperation? specificOperation,
        CancellationToken cancellationToken)
    {
        var decision = await EvaluateResourceDataEditorAsync(
            context, resource, service, specificOperation, cancellationToken).ConfigureAwait(false);
        return CreateDecisionResult(context, decision);
    }

    internal static async Task<AccessDecision> EvaluateResourceDataEditorAsync(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service,
        AuthorizationOperation? specificOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (!TenantScopeHelpers.IsTenantVisible(context, resource, service))
        {
            return AccessDecision.Forbidden(AccessPolicyHelpers.TenantScopeDeniedReason);
        }

        // Ownership semantics are a capability constraint, not a discretionary
        // grant. Admins, wildcard grants and scoped write keys cannot bypass it.
        if (MetadataV2RelationshipEditPolicy.RequiresReadOnly(resource))
        {
            return AccessDecision.Forbidden(MetadataV2RelationshipEditPolicy.ReadOnlyReason);
        }

        // Layer-scoped write keys (#1637) are enforced here in the shared pipeline.
        // The key must carry a grant for the target (service, layer); a service-wide
        // grant authorizes any layer of that service, a layer-specific grant only
        // the named layer. Out-of-scope targets are forbidden.
        if (LayerScopedWriteKey.IsScopedWritePrincipal(context.User))
        {
            return EvaluateScopedWriteKeyDecision(context, service?.Metadata.Name, resource.Metadata.Name);
        }

        // An explicit AccessPolicy write restriction stays authoritative (a
        // deliberate per-resource grant/deny must not be widened by a wildcard
        // RBAC grant), matching pre-#1376 behavior.
        var explicitDecision = EvaluateExplicitWritePolicy(context, resource.AccessPolicy, service?.AccessPolicy);
        if (explicitDecision is not null)
        {
            return explicitDecision.Value;
        }

        // No explicit write policy: a per-operation RBAC write grant (#1376) on
        // the (service, layer) authorizes the mutation, bypassing the coarse
        // data-editor role gate. When specificOperation is set (per-type edit checks
        // — BH3-001/BH3-014) the bypass is narrowed to that exact operation so a
        // caller with only an Insert grant cannot bypass the gate for an Update-only
        // payload and vice-versa.
        if (service is not null &&
            await HasWriteGrantAsync(context, service.Metadata.Name, resource.Metadata.Name, cancellationToken, specificOperation).ConfigureAwait(false))
        {
            return AccessDecision.Allowed();
        }

        if (service is null)
        {
            // No service context — fall back to the same global role gate used for
            // service-scoped checks (admin or global data-editor role).  Allowing any
            // authenticated principal here would let a principal with only the
            // default OIDC role mutate any service-less resource, bypassing RBAC.
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                return AccessDecision.RequiresAuth("Authentication is required.");
            }

            var options = context.RequestServices.GetRequiredService<IOptions<RbacOptions>>().Value;
            var user = context.User!; // non-null: authenticated guard above
            if (IsAdmin(user, options, context.RequestServices) ||
                HasGlobalDataEditorRole(user, options, context.RequestServices))
            {
                return AccessDecision.Allowed();
            }

            return AccessDecision.Forbidden("User does not have the required data editor role.");
        }

        return await EvaluateServiceAccessAsync(context, service.Metadata.Name, cancellationToken).ConfigureAwait(false);
    }

    internal static AccessDecision EvaluateScopedWriteKeyDecision(
        HttpContext context,
        string? serviceName,
        string? layerName)
    {
        if (!string.IsNullOrWhiteSpace(serviceName) &&
            LayerScopedWriteKey.AllowsWrite(context.User, serviceName, layerName))
        {
            return AccessDecision.Allowed();
        }

        return AccessDecision.Forbidden("The supplied write key is not scoped to this resource.");
    }

    // A service-scoped editor cannot authorize a tenant-wide mutation, because that mutation is shared by every service.
    internal static AccessDecision EvaluateGlobalDataEditor(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return AccessDecision.RequiresAuth("Authentication is required.");
        }

        var options = context.RequestServices.GetService<IOptions<RbacOptions>>()?.Value ?? new RbacOptions();
        if (IsAdmin(context.User, options, context.RequestServices) ||
            HasGlobalDataEditorRole(context.User, options, context.RequestServices))
        {
            return AccessDecision.Allowed();
        }

        return AccessDecision.Forbidden("User does not have the required data editor role.");
    }

    internal static Task<AccessDecision> EvaluateServiceAccessAsync(
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

        if (IsAdmin(context.User, options, context.RequestServices))
        {
            return Task.FromResult(AccessDecision.Allowed());
        }

        if (HasGlobalDataEditorRole(context.User, options, context.RequestServices))
        {
            return Task.FromResult(AccessDecision.Allowed());
        }

        if (HasServiceScopedRole(context.User, options, serviceId, context.RequestServices))
        {
            return Task.FromResult(AccessDecision.Allowed());
        }

        return Task.FromResult(AccessDecision.Forbidden("User does not have the required data editor role."));
    }

    // A layer-scoped write key is never widened by the coarse role checks, and a denial is 403 because the principal is already authenticated.
    private static IResult? EvaluateScopedWriteKey(
        HttpContext context,
        string? serviceName,
        string? layerName)
    {
        if (!string.IsNullOrWhiteSpace(serviceName) &&
            LayerScopedWriteKey.AllowsWrite(context.User, serviceName, layerName))
        {
            return null;
        }

        return CreateDecisionResult(
            context,
            AccessDecision.Forbidden("The supplied write key is not scoped to this resource."));
    }

    private static IResult? CreateDecisionResult(HttpContext context, AccessDecision decision)
        => AccessPolicyHelpers.CreateAccessDeniedResult(context, decision);

    private static async Task<bool> HasWriteGrantAsync(
        HttpContext context,
        string serviceName,
        string? layerName,
        CancellationToken cancellationToken,
        AuthorizationOperation? specificOperation = null)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return false;
        }

        var resolver = context.RequestServices.GetService<IPermissionResolver>();
        if (resolver is null)
        {
            return false;
        }

        var principal = context.User;
        var options = context.RequestServices.GetRequiredService<IOptions<RbacOptions>>().Value;
        var roles = RbacRoleClaims.Enumerate(principal, options, context.RequestServices);
        if (roles.Count == 0)
        {
            return false;
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? string.Empty;
        var isAuthenticated = principal.Identity?.IsAuthenticated == true;

        // When specificOperation is set (per-type edit check), only that operation
        // can bypass the data-editor role gate.  Without it, any write op suffices
        // (coarse caller check — BH3-001/BH3-014 narrowing is done at the call site).
        IEnumerable<AuthorizationOperation> operations = specificOperation.HasValue
            ? [specificOperation.Value]
            : WriteOperations;

        foreach (var operation in operations)
        {
            var decision = await resolver.AuthorizeAsync(
                userId,
                roles,
                serviceName,
                layerName,
                operation,
                isAuthenticated,
                cancellationToken).ConfigureAwait(false);
            if (decision.IsAllowed)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly AuthorizationOperation[] WriteOperations =
    [
        AuthorizationOperation.Insert,
        AuthorizationOperation.Update,
        AuthorizationOperation.Delete,
    ];

    private static AccessDecision? EvaluateExplicitWritePolicy(
        HttpContext context,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy)
    {
        if (!HasExplicitWritePolicy(layerPolicy) &&
            !HasExplicitWritePolicy(servicePolicy))
        {
            return null;
        }

        return AccessPolicyHelpers.EvaluateAccess(
            context,
            layerPolicy,
            servicePolicy,
            AccessScope.Write);
    }

    private static bool HasExplicitWritePolicy(AccessPolicy? policy)
        => policy is not null &&
           (policy.AllowAnonymousWrite ||
            policy.AllowedWriteRoles is { Length: > 0 } ||
            policy.AllowedRoles is { Length: > 0 });

    internal static bool IsAdminPrincipal(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<RbacOptions>>().Value;
        return IsAdmin(context.User, options, context.RequestServices);
    }

    internal static string? ResolveOwnerSubject(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(subject))
        {
            return subject;
        }

        subject = principal.FindFirst("sub")?.Value;
        if (!string.IsNullOrWhiteSpace(subject))
        {
            return subject;
        }

        subject = principal.FindFirst("api_key_id")?.Value;
        if (!string.IsNullOrWhiteSpace(subject))
        {
            return subject;
        }

        subject = principal.Identity?.Name;
        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }

    internal static EditPrincipal ResolveEditPrincipal(HttpContext? httpContext)
    {
        if (httpContext?.User?.Identity?.IsAuthenticated != true)
        {
            return EditPrincipal.Anonymous;
        }

        return new EditPrincipal(
            ResolveOwnerSubject(httpContext.User),
            IsAuthenticated: true,
            IsAdmin: IsAdminPrincipal(httpContext));
    }

    private static bool IsAdmin(
        ClaimsPrincipal principal,
        RbacOptions options,
        IServiceProvider serviceProvider)
        => AdminApiKeyPermission.IsFullAdminPrincipal(principal)
           || (!principal.FindAll(AdminApiKeyPermission.PermissionClaimType).Any()
               && RbacRoleClaims.IsAdmin(principal, options, serviceProvider));

    private static bool HasGlobalDataEditorRole(
        ClaimsPrincipal principal,
        RbacOptions options,
        IServiceProvider serviceProvider)
    {
        if (options.DataEditorRoles.Length == 0)
        {
            return false;
        }

        return RbacRoleClaims.Enumerate(principal, options, serviceProvider).Any(role =>
            options.DataEditorRoles.Any(allowed =>
                string.Equals(allowed?.Trim(), role, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasServiceScopedRole(
        ClaimsPrincipal principal,
        RbacOptions options,
        string serviceId,
        IServiceProvider serviceProvider)
    {
        var prefix = GetServiceScopedRolePrefix(options);

        var expected = string.Concat(prefix, serviceId);

        return RbacRoleClaims.Enumerate(principal, options, serviceProvider)
            .Any(role => string.Equals(role, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetServiceScopedRolePrefix(RbacOptions options)
    {
        return string.IsNullOrWhiteSpace(options.DataEditorServicePrefix)
            ? "data-editor:"
            : options.DataEditorServicePrefix.Trim();
    }

}

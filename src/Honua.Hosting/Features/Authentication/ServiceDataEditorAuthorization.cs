// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Infrastructure.Authentication;

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

    /// <summary>
    /// Per-edit-type variant of <see cref="RequireResourceDataEditorAsync(HttpContext, MetadataV2Resource, MetadataV2Service?, CancellationToken)"/>
    /// (BH3-001/BH3-014). The RBAC data-editor gate bypass is narrowed to the
    /// specified <paramref name="operation"/> so a caller with only an Insert grant
    /// is denied an Update-only payload and vice-versa.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="resource">The target resource.</param>
    /// <param name="service">The owning service, or <see langword="null"/>.</param>
    /// <param name="operation">The specific write operation being authorized.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An error result when denied; otherwise <see langword="null"/>.</returns>
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

    /// <summary>
    /// Decision-shaped core of the per-layer data-editor write gate, shared by the
    /// HTTP <see cref="IResult"/> wrappers above and by any non-HTTP-result adapter
    /// that surfaces denials through its own error contract. (The MCP surface
    /// exposes no feature-mutation tool per ADR-0028 — AI operational data editing
    /// is not supported — so this core currently backs only the human-facing HTTP
    /// edit adapters.) Semantics are identical to the HTTP path:
    /// layer-scoped write keys (#1637) are authoritative for scoped principals; an
    /// explicit <see cref="AccessPolicy"/> write restriction stays authoritative;
    /// otherwise a per-operation RBAC write grant (#1376) on the
    /// <c>(service, layer)</c> authorizes the mutation (narrowed to
    /// <paramref name="specificOperation"/> when set — BH3-001/BH3-014), falling
    /// back to the coarse admin/data-editor/service-scoped role gate.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="resource">The target resource (layer).</param>
    /// <param name="service">The owning service, or <see langword="null"/>.</param>
    /// <param name="specificOperation">The specific write operation being authorized, or <see langword="null"/> for any-write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The access decision.</returns>
    internal static async Task<AccessDecision> EvaluateResourceDataEditorAsync(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service,
        AuthorizationOperation? specificOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

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
            if (IsAdmin(user, options) || HasGlobalDataEditorRole(user, options))
            {
                return AccessDecision.Allowed();
            }

            return AccessDecision.Forbidden("User does not have the required data editor role.");
        }

        return await EvaluateServiceAccessAsync(context, service.Metadata.Name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates a layer-scoped write key (#1637) for non-HTTP adapters (such as
    /// gRPC) that consume an <see cref="AccessDecision"/> rather than an
    /// <see cref="IResult"/>. Returns an allowed decision only when a grant covers
    /// the target <c>(service, layer)</c>; otherwise forbidden.
    /// </summary>
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

    /// <summary>
    /// Authorizes a layer-scoped write key (#1637) against the target
    /// <c>(service, layer)</c>. Returns <see langword="null"/> when a grant
    /// authorizes the write; otherwise returns a 403 (the principal is already
    /// authenticated, so a denial is never a 401). Scoped keys are write-only and
    /// confer no admin or read authority, so they are never widened by the coarse
    /// role checks used for ordinary principals.
    /// </summary>
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

    /// <summary>
    /// Consults the canonical per-operation permission resolver (#1376) for any
    /// write-class grant (insert/update/delete) over the supplied
    /// <c>(service, layer)</c>. Returns <see langword="true"/> when an explicit
    /// grant authorizes a mutation, letting the write gate honor per-operation
    /// grants. When no resolver is registered, no roles are present, or no grant
    /// matches, returns <see langword="false"/> so the coarse data-editor gate
    /// applies unchanged.
    /// </summary>
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
        var roles = EnumerateRoles(principal, options).ToList();
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

    /// <summary>
    /// Resolves whether the request's principal holds the administrative override role.
    /// Used by the shared edit pipeline to let admins bypass owner-based edit policies
    /// (ownership-based access control, #2132).
    /// </summary>
    /// <param name="context">The current HTTP context carrying the authenticated principal.</param>
    /// <returns>True when the principal is an administrator.</returns>
    internal static bool IsAdminPrincipal(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<RbacOptions>>().Value;
        return IsAdmin(context.User, options);
    }

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

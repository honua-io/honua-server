// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Studio.Services;

public sealed class StudioAuthorizationService : IStudioAuthorizationService
{
    internal const string OwnResourceSentinel = "own";

    private const string AdminRole = "admin";
    public const string EndUserModeDisabledCode = "studio_authorization/end_user_mode_disabled";

    public const string CrossUserDeniedCode = "studio_authorization/cross_user_denied";

    public const string CrossTenantDeniedCode = "studio_authorization/cross_tenant_denied";

    public const string AuthenticationRequiredCode = "studio_authorization/authentication_required";

    public const string ElevatedGrantRequiredCode = "studio_authorization/elevated_grant_required";

    public const string OwnerAssignmentAdminRequiredCode = "studio_authorization/owner_assignment_admin_required";

    public const string OperatorGrantRequiredCode = "studio_authorization/operator_grant_required";

    public const string OAuthScopeRequiredCode = "studio_authorization/oauth_scope_required";

    public const string ScopeDeniedCode = "studio_authorization/insufficient_scope";

    private readonly IOperatorAuthorizationEvaluator _evaluator;
    private readonly IOperatorScopeAuthorizer _scopeAuthorizer;
    private readonly IOptionsMonitor<StudioEndUserAuthorizationOptions> _options;
    private readonly IOptionsMonitor<AdminRoleOptions> _adminRoleOptions;
    private readonly ITenantContext? _tenantContext;
    private readonly IOptionsMonitor<TenantIsolationOptions>? _tenantIsolationOptions;

    public StudioAuthorizationService(
        IOperatorAuthorizationEvaluator evaluator,
        IOperatorScopeAuthorizer scopeAuthorizer,
        IOptionsMonitor<StudioEndUserAuthorizationOptions> options,
        IOptionsMonitor<AdminRoleOptions> adminRoleOptions)
        : this(evaluator, scopeAuthorizer, options, adminRoleOptions, tenantContext: null, tenantIsolationOptions: null)
    {
    }

    public StudioAuthorizationService(
        IOperatorAuthorizationEvaluator evaluator,
        IOperatorScopeAuthorizer scopeAuthorizer,
        IOptionsMonitor<StudioEndUserAuthorizationOptions> options,
        IOptionsMonitor<AdminRoleOptions> adminRoleOptions,
        ITenantContext? tenantContext,
        IOptionsMonitor<TenantIsolationOptions>? tenantIsolationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(scopeAuthorizer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adminRoleOptions);
        _evaluator = evaluator;
        _scopeAuthorizer = scopeAuthorizer;
        _options = options;
        _adminRoleOptions = adminRoleOptions;
        _tenantContext = tenantContext;
        _tenantIsolationOptions = tenantIsolationOptions;
    }

    public bool IsEndUserAuthorizationEnabled => _options.CurrentValue.Enabled;

    public bool IsAdmin(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // The literal "admin" role is always recognized regardless of configuration -- this
        // matches every other admin check on the platform (AdminApiKeyPermission, AdminSession,
        // the API-key handler's role stamping) and must never regress even if a deployment
        // configures Oidc:AdminRoles to a list that omits it.
        if (principal.IsInRole(AdminRole))
        {
            return true;
        }

        // Also recognize configured OIDC admin-role aliases (for example "administrator"), the
        // same Oidc:AdminRoles-driven set that OidcAuthenticationExtensions.AddOidcAuthorization
        // widens AdminPolicy/AdminPolicyAlias/the Temporal-* policies with. See
        // AdminRoleOptions for why this reads the same config key rather than referencing that
        // type directly (Honua.Core cannot depend on Honua.Hosting).
        var aliases = _adminRoleOptions.CurrentValue.AdminRoles;
        if (aliases is null)
        {
            return false;
        }

        return aliases.Any(alias => !string.IsNullOrWhiteSpace(alias) && principal.IsInRole(alias));
    }

    public string? ResolveCallerId(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity is not { IsAuthenticated: true })
        {
            return null;
        }

        // Honua.Core has no ASP.NET dependency, so this uses ClaimsPrincipal.FindFirst rather
        // than the Microsoft.AspNetCore.Authentication FindFirstValue extension.
        var candidate = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = principal.FindFirst("sub")?.Value;
        }

        if (!string.IsNullOrWhiteSpace(candidate))
        {
            // A token subject is unique only within its issuer, and one issuer can mint the same
            // subject into several tenants. Bind issuer-bearing subjects to the
            // validated issuer and the request's resolved tenant so neither an equal subject from
            // another issuer nor the same subject in another tenant ever owns the same draft.
            // Issuer-less principals (API keys, framework sessions) keep their immutable id.
            var issuer = principal.FindFirst("iss")?.Value;
            // Deployment-default requests use tenantless owner keys when tenancy is disabled;
            // frozen execution bindings retain their recorded tenant component.
            var ownerTenant = _tenantIsolationOptions?.CurrentValue.Enabled == false
                && _tenantContext?.Source == TenantContextSource.Default
                    ? null
                    : _tenantContext?.TenantId;
            return string.IsNullOrWhiteSpace(issuer)
                ? candidate
                : $"subject:{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(candidate)}"
                    + $"@tenant:{Uri.EscapeDataString(ownerTenant ?? string.Empty)}";
        }

        candidate = principal.FindFirst("api_key_id")?.Value;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        candidate = principal.FindFirst("api_key_name")?.Value;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        candidate = principal.Identity.Name;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        candidate = principal.FindFirst(ClaimTypes.Name)?.Value;
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    public async Task<StudioAuthorizationDecision> AuthorizeAsync(
        ClaimsPrincipal principal,
        string? callerId,
        StudioAuthorizationOperation operation,
        string? resourceOwnerId,
        string? resourceTenantId,
        bool isPubliclyReadable = false,
        string? resourceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // OAuth scopes are an unconditional ceiling over role, grant, and ownership authority.
        // Keep this ahead of the admin bypass so Studio HTTP and MCP composition share the same
        // protocol-independent narrowing seam.
        var scopeDecision = _scopeAuthorizer.Evaluate(
            principal,
            OperatorResourceType.StudioDraft,
            MapToScopeOperation(operation));
        if (!scopeDecision.IsAllowed)
        {
            return StudioAuthorizationDecision.Deny(
                ScopeDeniedCode,
                scopeDecision.Reason
                    ?? $"The access token's scopes do not permit '{operation}' on Studio drafts.");
        }

        // Tenant ownership is an unconditional boundary over role, grant and ownership
        // authority. It sits ahead of the admin bypass on purpose: the
        // platform "admin" role is tenant-scoped, so a tenant-scoped administrator must never
        // read, enumerate or propose another tenant's Studio content. Only the configured
        // MultiTenancy:MultiTenantAdminRoles operate across tenants.
        if (!CanAccessTenant(principal, resourceTenantId))
        {
            return StudioAuthorizationDecision.Deny(
                CrossTenantDeniedCode,
                "The Studio resource belongs to another tenant.");
        }

        // Admins always have full, unscoped access, independent of the feature flag.
        if (IsAdmin(principal))
        {
            return StudioAuthorizationDecision.Allow();
        }

        if (!IsEndUserAuthorizationEnabled)
        {
            return StudioAuthorizationDecision.Deny(
                EndUserModeDisabledCode,
                "Studio package lifecycle operations require the admin role.");
        }

        if (principal.Identity is not { IsAuthenticated: true } || string.IsNullOrWhiteSpace(callerId))
        {
            return StudioAuthorizationDecision.Deny(
                AuthenticationRequiredCode,
                "Authentication is required for Studio package lifecycle operations.");
        }

        var isElevated = operation is StudioAuthorizationOperation.PublishRequest
            or StudioAuthorizationOperation.Rollback
            or StudioAuthorizationOperation.Generate;
        var isRead = operation is StudioAuthorizationOperation.ReadDraft
            or StudioAuthorizationOperation.ReadContentItem
            or StudioAuthorizationOperation.ListOwn;
        // Fail closed on an ownerless *existing* resource: owner_id is a nullable column
        // (legacy rows created before the ownership migration may still be
        // unbackfilled), so treating a null owner as "owned by whoever asks" would let any
        // authenticated caller claim it. A null owner here means "no owner assigned yet" --
        // only an admin may act on it until an owner is assigned. Endpoints that create a
        // brand-new resource never reach this method with a null resourceOwnerId; they pass
        // the actual (possibly just-created) owner id of an existing resource in every call
        // site (see StudioPackageEndpoints.EnsureAuthorizedAsync call sites).
        // Owner ids are canonical actor ids. Historical MCP session keys have no persisted
        // provenance marker that distinguishes them from a legitimate canonical subject with
        // the same text, so interpreting every owner as a possible legacy alias would create a
        // cross-principal collision. Unmarked legacy rows therefore fail closed and require an
        // explicit admin migration to a canonical owner id.
        var isOwn = resourceOwnerId is not null
            && string.Equals(resourceOwnerId, callerId, StringComparison.Ordinal);

        if (!isElevated)
        {
            // Baseline tier: ownership (or public-read visibility) alone authorizes the
            // operation. No operator grant is required -- the flag itself is the widening
            // switch for REQ-002's "non-admin sessions can complete the draft->version flow on
            // their own items".
            if (isOwn || (isRead && isPubliclyReadable))
            {
                return StudioAuthorizationDecision.Allow();
            }

            return StudioAuthorizationDecision.Deny(
                CrossUserDeniedCode,
                "The caller does not own this Studio resource.");
        }

        // Elevated tier (REQ-003): publish-request and rollback always require a matching
        // StudioDraft operator grant, evaluated through the platform's existing role/grant
        // infrastructure rather than a parallel mechanism -- ownership alone never suffices, and
        // (unlike the baseline tier) a cross-user caller is not rejected before the grant check
        // runs, since an operator-provisioned delegate grant is scoped to the concrete resource
        // id regardless of who owns it. A grant scoped to the "own" sentinel authorizes every
        // resource the caller owns; a grant scoped to the concrete resourceId (or the "*"
        // wildcard) authorizes an operator-provisioned delegate, independent of ownership.
        var operatorOperation = operation switch
        {
            StudioAuthorizationOperation.PublishRequest => OperatorOperation.Publish,
            StudioAuthorizationOperation.Generate => OperatorOperation.Execute,
            _ => OperatorOperation.Rollback,
        };

        if (isOwn && await HasOperatorGrantAsync(principal, operatorOperation, OwnResourceSentinel, cancellationToken).ConfigureAwait(false))
        {
            return StudioAuthorizationDecision.Allow(elevated: true);
        }

        if (resourceId is not null
            && await HasOperatorGrantAsync(principal, operatorOperation, resourceId, cancellationToken).ConfigureAwait(false))
        {
            return StudioAuthorizationDecision.Allow(elevated: true);
        }

        if (!isOwn)
        {
            return StudioAuthorizationDecision.Deny(
                CrossUserDeniedCode,
                "The caller does not own this Studio resource and holds no delegate operator grant for it.",
                elevated: true);
        }

        return StudioAuthorizationDecision.Deny(
            ElevatedGrantRequiredCode,
            $"'{operation}' requires a StudioDraft '{operatorOperation}' operator grant.",
            elevated: true);
    }

    private bool CanAccessTenant(ClaimsPrincipal principal, string? resourceTenantId)
    {
        if (_tenantContext is null)
        {
            return true;
        }

        var options = _tenantIsolationOptions?.CurrentValue ?? new TenantIsolationOptions();
        return TenantOwnership.IsMultiTenantAdmin(principal, options)
            || TenantOwnership.CanAccess(resourceTenantId, _tenantContext.TenantId, options);
    }

    public TenantScopeFilter? CreateTenantScopeFilter(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (_tenantContext is null)
        {
            return null;
        }

        return TenantOwnership.CreateScopeFilter(
            principal,
            _tenantContext.TenantId,
            _tenantIsolationOptions?.CurrentValue ?? new TenantIsolationOptions());
    }

    private static OperatorOperation MapToScopeOperation(StudioAuthorizationOperation operation)
        => operation switch
        {
            StudioAuthorizationOperation.ReadDraft
                or StudioAuthorizationOperation.ReadContentItem
                or StudioAuthorizationOperation.ListOwn
                or StudioAuthorizationOperation.ValidateDraft => OperatorOperation.Read,
            StudioAuthorizationOperation.PublishRequest => OperatorOperation.Publish,
            StudioAuthorizationOperation.Generate => OperatorOperation.Execute,
            StudioAuthorizationOperation.Rollback => OperatorOperation.Rollback,
            StudioAuthorizationOperation.UpdateDraft => OperatorOperation.Update,
            StudioAuthorizationOperation.DeleteDraft => OperatorOperation.Delete,
            _ => OperatorOperation.Create,
        };

    private async Task<bool> HasOperatorGrantAsync(
        ClaimsPrincipal principal,
        OperatorOperation operation,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var decision = await _evaluator.EvaluateAsync(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.StudioDraft,
                ResourceId = resourceId,
                Operation = operation,
            },
            cancellationToken).ConfigureAwait(false);
        return decision.IsAllowed;
    }

}

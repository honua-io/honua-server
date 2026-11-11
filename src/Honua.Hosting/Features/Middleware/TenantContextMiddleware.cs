// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Middleware;

// A tenant header is accepted only from a configured multi-tenant admin role; everyone else uses a claim, then the anonymous default.
internal sealed class TenantContextMiddleware(
    RequestDelegate next,
    IOptions<TenantContextOptions> options,
    ILogger<TenantContextMiddleware> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<TenantContextMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TenantContextOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task InvokeAsync(HttpContext context)
    {
        var principal = context.User;
        var isAuthenticated = principal?.Identity?.IsAuthenticated == true;

        // Approved replay has already sealed its tenant. Honor the authenticated
        // credential binding even if header/claim configuration changed after approval.
        if (isAuthenticated && principal!.Identity?.AuthenticationType == AuthenticationExtensions.ApiKeyScheme &&
            principal.IsInRole(AdminApiKeyPermission.ApprovedOperationRole) &&
            principal.FindFirst("api_key_id") is not null)
        {
            var approvedTenant = principal.FindFirst(AdminApiKeyPermission.ApprovedOperationTenantClaim)?.Value;
            var approvedContext = context.RequestServices.GetService<ITenantContext>() as RequestTenantContext;
            if (approvedContext is null || approvedTenant is null ||
                approvedTenant.Length > _options.MaxTenantIdLength || !IsSafeTenantId(approvedTenant))
            {
                await TenantDenialResponseWriter.WriteAsync(context, TenantDenialKind.AuthenticationRequired).ConfigureAwait(false);
                return;
            }

            approvedContext.Set(approvedTenant, TenantContextSource.Claim);
            CanonicalSecurityActor.StampRequestBinding(principal, approvedContext.TenantId);
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!_options.Enabled)
        {
            // Single-tenant mode deliberately has no effective tenant, but bearer
            // audit and durable-job attribution must still use the validator-owned,
            // issuer-qualified actor rather than falling back to a bare subject.
            if (isAuthenticated && CanonicalSecurityActor.IsBearerPrincipal(principal!))
            {
                CanonicalSecurityActor.StampRequestBinding(principal!, effectiveTenant: null);
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        // Resolve the per-request context object via DI so handlers see the same instance.
        var tenantContext = context.RequestServices.GetService<ITenantContext>() as RequestTenantContext;
        if (tenantContext is null)
        {
            // Tenant context was not registered (e.g. tests that did not call
            // AddHonuaTenantContext); skip resolution rather than fail the request.
            await _next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value;
        // Job capabilities already carry a validated, frozen owner tenant. OIDC
        // claim-name configuration and the anonymous default must never replace it.
        if (isAuthenticated && principal!.Identity?.AuthenticationType == ScopedJobAuthentication.Scheme)
        {
            var frozenTenants = principal.FindAll(ScopedJobTokenIssuer.TenantClaimType).ToArray();
            var frozenTenant = frozenTenants.Length == 1 ? frozenTenants[0].Value : null;
            if (frozenTenants.Length > 1 || (frozenTenant is not null &&
                (string.IsNullOrWhiteSpace(frozenTenant) || frozenTenant.Length > _options.MaxTenantIdLength ||
                 !IsSafeTenantId(frozenTenant))))
            {
                await TenantDenialResponseWriter.WriteAsync(context, TenantDenialKind.AuthenticationRequired).ConfigureAwait(false);
                return;
            }

            tenantContext.Set(frozenTenant, TenantContextSource.Claim);
            CanonicalSecurityActor.StampRequestBinding(principal, frozenTenant);
            await _next(context).ConfigureAwait(false);
            return;
        }

        // 1. Header override (admin-only). Evaluate first so a logged-in admin can target
        //    a specific tenant explicitly even if they also carry a tid claim.
        if (TryReadHeader(context, out var headerTenantId))
        {
            if (isAuthenticated && PrincipalHasMultiTenantAdminRole(principal!))
            {
                tenantContext.Set(headerTenantId, TenantContextSource.Header);
                CanonicalSecurityActor.StampRequestBinding(principal!, headerTenantId);
                TenantContextLog.HeaderOverrideAccepted(
                    _logger,
                    GetPrincipalIdentifier(principal),
                    headerTenantId,
                    path);
                await _next(context).ConfigureAwait(false);
                return;
            }

            // Header present but caller is not authorized — log and fall through to claim
            // resolution so we never silently grant cross-tenant access.
            TenantContextLog.HeaderOverrideRejected(
                _logger,
                GetPrincipalIdentifier(principal),
                TenantContextOptions.TenantHeaderName,
                path);

            // A bearer caller with an unauthorized override must fail closed. Falling
            // through to a claim or default tenant would make the request appear valid
            // to downstream middleware while silently discarding the caller's choice.
            if (isAuthenticated && CanonicalSecurityActor.IsBearerPrincipal(principal!))
            {
                // Audit still needs the issuer-qualified actor even though the rejected
                // override has no accepted effective tenant.
                CanonicalSecurityActor.StampRequestBinding(principal!, effectiveTenant: null);
                await TenantDenialResponseWriter
                    .WriteAsync(context, TenantDenialKind.PermissionDenied)
                    .ConfigureAwait(false);
                return;
            }
        }

        // 2. Claim-based resolution.
        if (isAuthenticated && TryReadClaim(principal!, out var claimTenantId))
        {
            tenantContext.Set(claimTenantId, TenantContextSource.Claim);
            CanonicalSecurityActor.StampRequestBinding(principal!, claimTenantId);
            TenantContextLog.Resolved(_logger, nameof(TenantContextSource.Claim), claimTenantId, path);
            await _next(context).ConfigureAwait(false);
            return;
        }

        // External bearers without a validated tenant claim never inherit the default
        // tenant (see RejectTenantlessBearerAsync). Operator bearers are the exception: the
        // server mints them from an administrator session with its claims copied verbatim,
        // so they take the default tenant as that session's cookie does. They differ from
        // the cookie in one place: an unauthorized tenant header is denied (step 1) rather
        // than ignored.
        var hasDefaultTenant = !string.IsNullOrWhiteSpace(_options.DefaultTenantId);
        var isBearer = isAuthenticated && CanonicalSecurityActor.IsBearerPrincipal(principal!);
        if (isBearer && !(hasDefaultTenant && CanonicalSecurityActor.IsOperatorBearerOnlyPrincipal(principal!)))
        {
            await RejectTenantlessBearerAsync(context, principal!, tenantContext).ConfigureAwait(false);
            return;
        }

        // 3. Default tenant: anonymous public reads (CITE / unauthenticated OGC), session
        //    cookies and operator bearers without a tenant claim.
        if (hasDefaultTenant)
        {
            tenantContext.Set(_options.DefaultTenantId, TenantContextSource.Default);
            if (isBearer)
            {
                CanonicalSecurityActor.StampRequestBinding(principal!, _options.DefaultTenantId);
            }

            TenantContextLog.Resolved(_logger, nameof(TenantContextSource.Default), _options.DefaultTenantId!, path);
            await _next(context).ConfigureAwait(false);
            return;
        }

        // 4. Anonymous / no default — leave context with null tenant id.
        tenantContext.Set(null, TenantContextSource.Anonymous);
        TenantContextLog.AnonymousNoDefault(_logger, path);
        await _next(context).ConfigureAwait(false);
    }

    private async Task RejectTenantlessBearerAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        RequestTenantContext tenantContext)
    {
        tenantContext.Set(null, TenantContextSource.Anonymous);
        CanonicalSecurityActor.StampRequestBinding(principal, null);

        // A null tenant on a tenant-bound route would let schema/data middleware fall back
        // to the deployment default. Only explicitly tenant-independent control-plane routes
        // and MCP discovery continue; MCP data-bearing operations enforce their own tenant
        // requirement before lookup or execution.
        var tenantBoundMcpCall = await TenantDenialResponseWriter
            .IsTenantBoundMcpRequestAsync(context.Request)
            .ConfigureAwait(false);
        if ((!context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase) || tenantBoundMcpCall)
            && !IsTenantIndependentControlPlaneEndpoint(context))
        {
            await TenantDenialResponseWriter
                .WriteAsync(context, TenantDenialKind.AuthenticationRequired)
                .ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private bool TryReadHeader(HttpContext context, out string tenantId)
    {
        if (!context.Request.Headers.TryGetValue(TenantContextOptions.TenantHeaderName, out StringValues raw)
            || StringValues.IsNullOrEmpty(raw))
        {
            tenantId = string.Empty;
            return false;
        }

        var candidate = raw.ToString().Trim();
        if (candidate.Length == 0)
        {
            tenantId = string.Empty;
            return false;
        }

        if (candidate.Length > _options.MaxTenantIdLength)
        {
            TenantContextLog.RejectedOversizedTenantValue(
                _logger, candidate.Length, nameof(TenantContextSource.Header), context.Request.Path.Value);
            tenantId = string.Empty;
            return false;
        }

        if (!IsSafeTenantId(candidate))
        {
            tenantId = string.Empty;
            return false;
        }

        tenantId = candidate;
        return true;
    }

    private bool TryReadClaim(ClaimsPrincipal principal, out string tenantId)
    {
        foreach (var claimType in _options.TenantClaimTypes)
        {
            if (string.IsNullOrWhiteSpace(claimType))
            {
                continue;
            }

            var claim = principal.FindFirst(claimType);
            if (claim is null || string.IsNullOrWhiteSpace(claim.Value))
            {
                continue;
            }

            var value = claim.Value.Trim();
            if (value.Length > _options.MaxTenantIdLength)
            {
                TenantContextLog.RejectedOversizedTenantValue(
                    _logger, value.Length, nameof(TenantContextSource.Claim), path: null);
                continue;
            }

            if (!IsSafeTenantId(value))
            {
                continue;
            }

            tenantId = value;
            return true;
        }

        tenantId = string.Empty;
        return false;
    }

    private bool PrincipalHasMultiTenantAdminRole(ClaimsPrincipal principal)
    {
        if (_options.MultiTenantAdminRoles.Length == 0)
        {
            return false;
        }

        return _options.MultiTenantAdminRoles.Any(
            role => !string.IsNullOrWhiteSpace(role) && principal.IsInRole(role));
    }

    private static string? GetPrincipalIdentifier(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is null || !principal.Identity.IsAuthenticated)
        {
            return null;
        }

        return principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value
            ?? principal.Identity.Name;
    }

    private static bool IsTenantIndependentControlPlaneEndpoint(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<TenantIndependentControlPlaneMetadata>() is not null;

    // Tenant ids should be safe to embed in logs and downstream SQL filters. Allow the
    // same character set as correlation ids — letters, digits, hyphen, underscore, dot,
    // colon, plus '@' to accommodate common tenant naming patterns (e.g. email-style).
    private static bool IsSafeTenantId(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var ok = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '-' || c == '_' || c == '.' || c == ':' || c == '@';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}

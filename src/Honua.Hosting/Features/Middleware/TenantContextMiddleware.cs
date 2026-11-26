// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Diagnostics;
using System.Security.Claims;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Middleware;

internal interface ITenantLogContextScopeProvider
{
    IDisposable PushTenantId(string tenantId);
}

// Multi-tenant mode accepts header overrides only from configured admin roles.
internal sealed class TenantContextMiddleware(
    RequestDelegate next,
    IOptions<TenantContextOptions> options,
    ILogger<TenantContextMiddleware> logger)
{
    private static readonly SearchValues<char> SafeTenantIdChars = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.:@");

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<TenantContextMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TenantContextOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task InvokeAsync(HttpContext context)
    {
        if (await TryHandleApprovedOperationAsync(context).ConfigureAwait(false))
        {
            return;
        }

        // Resolve the per-request context object via DI so handlers see the same instance.
        if (context.RequestServices.GetService<ITenantContext>() is not RequestTenantContext tenantContext)
        {
            // Tenant context was not registered (e.g. tests that did not call
            // AddHonuaTenantContext); skip resolution rather than fail the request.
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (await TryHandleScopedJobAsync(context, tenantContext).ConfigureAwait(false))
        {
            return;
        }

        if (!_options.Enabled)
        {
            await HandleSingleTenantAsync(context, tenantContext).ConfigureAwait(false);
            return;
        }

        if (await TryHandleHeaderOverrideAsync(context, tenantContext).ConfigureAwait(false))
        {
            return;
        }

        await ResolveClaimOrDefaultTenantAsync(context, tenantContext).ConfigureAwait(false);
    }

    // Approved replay has already sealed its tenant. Honor the authenticated
    // credential binding even if header/claim configuration changed after approval.
    private async Task<bool> TryHandleApprovedOperationAsync(HttpContext context)
    {
        var principal = context.User;
        if (principal?.Identity?.IsAuthenticated != true
            || principal.Identity.AuthenticationType != AuthenticationExtensions.ApiKeyScheme
            || !principal.IsInRole(AdminApiKeyPermission.ApprovedOperationRole)
            || principal.FindFirst("api_key_id") is null)
        {
            return false;
        }

        var approvedTenant = principal.FindFirst(AdminApiKeyPermission.ApprovedOperationTenantClaim)?.Value;
        if (context.RequestServices.GetService<ITenantContext>() is not RequestTenantContext approvedContext ||
            approvedTenant is null ||
            approvedTenant.Length > _options.MaxTenantIdLength || !IsSafeTenantId(approvedTenant))
        {
            await TenantDenialResponseWriter.WriteAsync(context, TenantDenialKind.AuthenticationRequired).ConfigureAwait(false);
            return true;
        }

        approvedContext.Set(approvedTenant, TenantContextSource.Claim);
        CanonicalSecurityActor.StampRequestBinding(principal, approvedContext.TenantId);
        await InvokeNextWithTenantTelemetryAsync(context, approvedContext.TenantId).ConfigureAwait(false);
        return true;
    }

    // Job capabilities already carry a validated, frozen owner tenant. OIDC
    // claim-name configuration and the anonymous default must never replace it.
    private async Task<bool> TryHandleScopedJobAsync(HttpContext context, RequestTenantContext tenantContext)
    {
        var principal = context.User;
        if (principal?.Identity?.IsAuthenticated != true
            || principal.Identity.AuthenticationType != ScopedJobAuthentication.Scheme)
        {
            return false;
        }

        var (frozenTenant, hasMultiple) = ReadFrozenJobTenant(principal);
        if (hasMultiple || (frozenTenant is not null &&
            (string.IsNullOrWhiteSpace(frozenTenant) || frozenTenant.Length > _options.MaxTenantIdLength ||
             !IsSafeTenantId(frozenTenant))))
        {
            await TenantDenialResponseWriter.WriteAsync(context, TenantDenialKind.AuthenticationRequired).ConfigureAwait(false);
            return true;
        }

        tenantContext.Set(frozenTenant, TenantContextSource.Claim);
        CanonicalSecurityActor.StampRequestBinding(principal, frozenTenant);
        await InvokeNextWithTenantTelemetryAsync(context, frozenTenant).ConfigureAwait(false);
        return true;
    }

    private static (string? TenantId, bool HasMultiple) ReadFrozenJobTenant(ClaimsPrincipal principal)
    {
        string? frozenTenant = null;
        foreach (var claim in principal.Claims)
        {
            if (!string.Equals(claim.Type, ScopedJobTokenIssuer.TenantClaimType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (frozenTenant is not null)
            {
                return (frozenTenant, true);
            }

            frozenTenant = claim.Value;
        }

        return (frozenTenant, false);
    }

    // Single-tenant deployments use the configured deployment tenant for every
    // request. Provider directory claims and tenant headers do not select a tenant.
    // Frozen replay/job bindings above remain authoritative in either mode.
    private async Task HandleSingleTenantAsync(HttpContext context, RequestTenantContext tenantContext)
    {
        var tenant = string.IsNullOrWhiteSpace(_options.DefaultTenantId) ? null : _options.DefaultTenantId;
        tenantContext.SetSingleTenantDefault(tenant);
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            CanonicalSecurityActor.StampRequestBinding(context.User, tenant);
        }

        await InvokeNextWithTenantTelemetryAsync(context, tenant).ConfigureAwait(false);
    }

    // 1. Header override (admin-only). Evaluate first so a logged-in admin can target
    //    a specific tenant explicitly even if they also carry a tid claim.
    private async Task<bool> TryHandleHeaderOverrideAsync(HttpContext context, RequestTenantContext tenantContext)
    {
        if (!TryReadHeader(context, out var headerTenantId))
        {
            return false;
        }

        var principal = context.User;
        var isAuthenticated = principal?.Identity?.IsAuthenticated == true;
        var path = context.Request.Path.Value;
        if (isAuthenticated && PrincipalHasMultiTenantAdminRole(principal!))
        {
            tenantContext.Set(headerTenantId, TenantContextSource.Header);
            CanonicalSecurityActor.StampRequestBinding(principal!, headerTenantId);
            TenantContextLog.HeaderOverrideAccepted(_logger, GetPrincipalIdentifier(principal), headerTenantId, path);
            await InvokeNextWithTenantTelemetryAsync(context, headerTenantId).ConfigureAwait(false);
            return true;
        }

        // Header present but caller is not authorized — log and fall through to claim
        // resolution so we never silently grant cross-tenant access.
        TenantContextLog.HeaderOverrideRejected(_logger, GetPrincipalIdentifier(principal), TenantContextOptions.TenantHeaderName, path);

        // A bearer caller with an unauthorized override must fail closed. Falling
        // through to a claim or default tenant would make the request appear valid
        // to downstream middleware while silently discarding the caller's choice.
        if (isAuthenticated && CanonicalSecurityActor.IsBearerPrincipal(principal!))
        {
            // Audit still needs the issuer-qualified actor even though the rejected
            // override has no accepted effective tenant.
            CanonicalSecurityActor.StampRequestBinding(principal!, effectiveTenant: null);
            await TenantDenialResponseWriter.WriteAsync(context, TenantDenialKind.PermissionDenied).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task ResolveClaimOrDefaultTenantAsync(HttpContext context, RequestTenantContext tenantContext)
    {
        var principal = context.User;
        var isAuthenticated = principal?.Identity?.IsAuthenticated == true;
        var path = context.Request.Path.Value;

        // 2. Claim-based resolution.
        if (isAuthenticated && TryReadClaim(principal!, out var claimTenantId))
        {
            tenantContext.Set(claimTenantId, TenantContextSource.Claim);
            CanonicalSecurityActor.StampRequestBinding(principal!, claimTenantId);
            TenantContextLog.Resolved(_logger, nameof(TenantContextSource.Claim), claimTenantId, path);
            await InvokeNextWithTenantTelemetryAsync(context, claimTenantId).ConfigureAwait(false);
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

        await HandleDefaultOrAnonymousTenantAsync(context, tenantContext).ConfigureAwait(false);
    }

    private async Task HandleDefaultOrAnonymousTenantAsync(HttpContext context, RequestTenantContext tenantContext)
    {
        var path = context.Request.Path.Value;
        // 3. Default tenant: anonymous public reads (CITE / unauthenticated OGC), any
        //    non-bearer caller and operator bearers without a tenant claim.
        if (!string.IsNullOrWhiteSpace(_options.DefaultTenantId))
        {
            tenantContext.Set(_options.DefaultTenantId, TenantContextSource.Default);
            if (context.User?.Identity?.IsAuthenticated == true && CanonicalSecurityActor.IsBearerPrincipal(context.User))
            {
                CanonicalSecurityActor.StampRequestBinding(context.User, _options.DefaultTenantId);
            }

            TenantContextLog.Resolved(_logger, nameof(TenantContextSource.Default), _options.DefaultTenantId, path);
            await InvokeNextWithTenantTelemetryAsync(context, _options.DefaultTenantId).ConfigureAwait(false);
            return;
        }

        // 4. Anonymous / no default — leave context with null tenant id.
        tenantContext.Set(null, TenantContextSource.Anonymous);
        TenantContextLog.AnonymousNoDefault(_logger, path);
        await _next(context).ConfigureAwait(false);
    }

    private async Task InvokeNextWithTenantTelemetryAsync(HttpContext context, string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        Activity.Current?.SetTag(HonuaTelemetry.Tags.TenantId, tenantId);
        using var loggerScope = _logger.BeginScope(CreateTenantLogScope(tenantId));
        using var logContextScope = context.RequestServices
            .GetService<ITenantLogContextScopeProvider>()
            ?.PushTenantId(tenantId);
        await _next(context).ConfigureAwait(false);
    }

    private static KeyValuePair<string, object?>[] CreateTenantLogScope(string tenantId)
        => [new KeyValuePair<string, object?>("TenantId", tenantId)];

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
        foreach (var role in _options.MultiTenantAdminRoles)
        {
            if (!string.IsNullOrWhiteSpace(role) && principal.IsInRole(role))
            {
                return true;
            }
        }

        return false;
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
        => value.AsSpan().IndexOfAnyExcept(SafeTenantIdChars) < 0;
}

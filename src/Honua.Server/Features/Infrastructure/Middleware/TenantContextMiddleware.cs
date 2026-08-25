// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Resolves the tenant context for the current request and stores it on the request
/// scope (issue #1144). Does not enforce tenant filtering — that is the responsibility
/// of downstream feature handlers that read <see cref="ITenantContext"/>.
/// </summary>
/// <remarks>
/// Resolution order:
///  1. <c>X-Honua-Tenant</c> header — accepted only when the authenticated principal
///     holds one of the configured multi-tenant admin roles. Header overrides emit a
///     warning-level audit log entry.
///  2. The first non-empty claim from <see cref="TenantContextOptions.TenantClaimTypes"/>
///     on the authenticated principal.
///  3. The configured <see cref="TenantContextOptions.DefaultTenantId"/> for
///     anonymous public reads (preserves CITE-compliant unauthenticated OGC behavior).
///  4. Otherwise the context remains <see cref="TenantContextSource.Anonymous"/> with a
///     <see langword="null"/> tenant id.
/// </remarks>
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
        if (!_options.Enabled)
        {
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
        var principal = context.User;
        var isAuthenticated = principal?.Identity?.IsAuthenticated == true;

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
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
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

        // An authenticated bearer without a validated tenant claim must never inherit
        // the anonymous public default. Tenant-required operations will reject the
        // null context downstream rather than running under the wrong tenant.
        if (isAuthenticated && CanonicalSecurityActor.IsBearerPrincipal(principal!))
        {
            tenantContext.Set(null, TenantContextSource.Anonymous);
            CanonicalSecurityActor.StampRequestBinding(principal!, null);

            // External OIDC bearers cannot enter tenant-bound routes with a null
            // tenant: schema/data middleware would otherwise use the deployment's
            // configured default. Only explicitly tenant-independent control-plane
            // routes may continue without a tenant; other admin routes can read or
            // mutate tenant-bound metadata. Honua's separately validated
            // OperatorBearer remains available for tenantless operator-only routes;
            // MCP data-bearing operations apply their own tenant requirement to that
            // scheme.
            if (CanonicalSecurityActor.IsTenantScopedBearerPrincipal(principal!)
                && !context.Request.Path.StartsWithSegments(
                    "/mcp",
                    StringComparison.OrdinalIgnoreCase)
                && !IsTenantIndependentControlPlaneEndpoint(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // MCP intentionally keeps tenantless discovery separate from data-bearing
            // operations. Its shared tool/resource authorization helpers enforce the
            // tenant requirement before lookup or execution.
            await _next(context).ConfigureAwait(false);
            return;
        }

        // 3. Default tenant fallback for anonymous public reads (CITE / unauthenticated OGC).
        if (!string.IsNullOrWhiteSpace(_options.DefaultTenantId))
        {
            tenantContext.Set(_options.DefaultTenantId, TenantContextSource.Default);
            TenantContextLog.Resolved(_logger, nameof(TenantContextSource.Default), _options.DefaultTenantId!, path);
            await _next(context).ConfigureAwait(false);
            return;
        }

        // 4. Anonymous / no default — leave context with null tenant id.
        tenantContext.Set(null, TenantContextSource.Anonymous);
        TenantContextLog.AnonymousNoDefault(_logger, path);
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

/// <summary>
/// Marks a control-plane endpoint whose data is global rather than tenant-scoped, allowing a
/// validated external bearer to reach endpoint authorization without an effective tenant.
/// </summary>
internal sealed class TenantIndependentControlPlaneMetadata
{
    internal static TenantIndependentControlPlaneMetadata Instance { get; } = new();

    private TenantIndependentControlPlaneMetadata()
    {
    }
}

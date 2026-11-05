// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp;

internal static class McpAuthorizationHelper
{
    private const string AnonymousPrincipalScheme = "anonymous";

    public static ClaimsPrincipal EnsurePrincipal(HttpContext context)
    {
        var principal = context.User;
        if (principal.Identity is null || !principal.Identity.IsAuthenticated)
        {
            throw new Geoprocessing.GeoprocessingAuthorizationException(requiresAuthentication: true);
        }

        return principal;
    }

    // Display names are never used once a durable subject or API-key identity is present.
    public static string ResolveActorId(ClaimsPrincipal principal)
        => CanonicalSecurityActor.Resolve(principal)?.ActorId
            ?? throw new InvalidOperationException("The authenticated MCP actor has no canonical identity.");

    public static string ResolvePrincipalKey(ClaimsPrincipal? principal)
    {
        var identity = ResolveAuthenticatedIdentity(principal);
        if (identity is null || !identity.IsAuthenticated)
        {
            return McpSessionManager.AnonymousPrincipalKey;
        }

        var scheme = ResolveScheme(identity);
        var subject = principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(subject))
        {
            return $"{scheme}:sub:{subject}";
        }

        return string.IsNullOrEmpty(identity.Name)
            ? $"{scheme}:authenticated"
            : $"{scheme}:name:{identity.Name}";
    }

    private static ClaimsIdentity? ResolveAuthenticatedIdentity(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true
            ? principal.Identity as ClaimsIdentity
            : principal?.Identities.FirstOrDefault(candidate => candidate.IsAuthenticated);

    private static string ResolveScheme(ClaimsIdentity identity) =>
        string.IsNullOrWhiteSpace(identity.AuthenticationType)
            ? AnonymousPrincipalScheme
            : identity.AuthenticationType;

    // Issuer, subject, tenant, and scope are taken from the validated token, never from request headers or parameters.
    public static string? ResolveSessionBindingKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var actor = CanonicalSecurityActor.Resolve(context.User);
        if (actor is null)
        {
            if (context.User.Identities.Any(static identity => identity.IsAuthenticated))
            {
                // Never collapse an authenticated caller whose validator supplied no
                // durable actor identity into the anonymous session namespace.
                return null;
            }

            return McpSessionManager.AnonymousPrincipalKey;
        }

        string? authorityFingerprint = null;
        if (CanonicalSecurityActor.IsBearerPrincipal(context.User))
        {
            if (!actor.IsDurablyRevalidatable || string.IsNullOrWhiteSpace(actor.SubjectIssuer))
            {
                // Display names are mutable, and a subject without its validated issuer is
                // not a globally durable OIDC session identifier. Bearers require both.
                return null;
            }

            authorityFingerprint = ResolveBearerAuthorityFingerprint(context);
            if (authorityFingerprint is null)
            {
                // A bearer principal is only ever projected from a presented Authorization
                // credential; without one the principal's provenance is unproven.
                return null;
            }
        }

        var tenant = context.RequestServices.GetService<ITenantContext>()?.TenantId
            ?? CanonicalSecurityActor.FindStampedValue(
                context.User,
                CanonicalSecurityActor.EffectiveTenantClaim);
        return CanonicalSecurityActor.BuildBindingKey(
            actor,
            tenant,
            context.User,
            authorityFingerprint);
    }

    private static string? ResolveBearerAuthorityFingerprint(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var credential = authorization[bearerPrefix.Length..].Trim();
        if (credential.Length == 0 || credential.Contains(','))
        {
            return null;
        }

        // Bind to what the validated credential authorizes, not to the credential bytes:
        // an access token refreshed with identical authority must keep its session, while
        // any change of roles, groups, permissions or other grants must not (#4909).
        return CanonicalSecurityActor.ResolveAuthorityFingerprint(context.User);
    }

    public static async Task EnsureBearerDataTenantAsync(HttpContext context, string target)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!CanonicalSecurityActor.IsBearerPrincipal(context.User))
        {
            return;
        }

        // A deployment that explicitly disables tenant resolution uses one configured
        // database/schema for every protocol. In that supported mode a null tenant is
        // not an unresolved authority boundary, so MCP follows the same single-tenant
        // data path as the other bearer-backed protocols. Missing options remain
        // fail-closed; only the explicit Enabled=false policy bypasses this requirement.
        if (context.RequestServices.GetService<IOptions<TenantContextOptions>>()?.Value.Enabled == false)
        {
            return;
        }

        var tenant = context.RequestServices.GetService<ITenantContext>()?.TenantId;
        if (string.IsNullOrWhiteSpace(tenant))
        {
            var auditLog = context.RequestServices.GetService<IAuditLog>();
            if (auditLog is not null)
            {
                var timeProvider = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
                await auditLog.RecordAsync(
                    new AuditEvent
                    {
                        Timestamp = timeProvider.GetUtcNow(),
                        EventType = AuditEventType.Authorization,
                        Actor = AuditContextResolver.ResolveActor(context, out var actorType),
                        ActorType = actorType,
                        ResourceType = "mcp",
                        ResourceId = target,
                        Action = "mcp.authorization",
                        Outcome = AuditOutcome.Denied,
                        CorrelationId = AuditContextResolver.ResolveCorrelationId(context),
                        RemoteIp = AuditContextResolver.ResolveRemoteIp(context),
                        UserAgent = AuditContextResolver.ResolveUserAgent(context),
                        Details = "{\"code\":\"tenant_required\"}",
                    },
                    context.RequestAborted).ConfigureAwait(false);
            }

            throw new Geoprocessing.GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                "A validated tenant is required to invoke MCP tools.");
        }
    }
}

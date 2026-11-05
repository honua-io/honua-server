// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using System.Security.Claims;

namespace Honua.Ai.Protocols.Mcp;

internal static class McpBearerAuthenticationEndpointExtensions
{
    private const string BearerPrefix = "Bearer ";
    private static readonly object AuthenticationFailureKey = new();
    private static readonly object ValidatedPrincipalKey = new();

    // The endpoint filter is defense in depth and is not the authority boundary.
    public static IApplicationBuilder UseMcpBearerAuthentication(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (httpContext, next) =>
        {
            if (!IsMcpTransportPath(httpContext.Request.Path)
                || !HasBearerCredential(httpContext))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var authenticationScheme = ResolveBearerAuthenticationScheme(httpContext);
            if (authenticationScheme is null)
            {
                // No matching operator credential and no external authority means no
                // bearer identity. Preserve the documented anonymous discovery behavior.
                await next().ConfigureAwait(false);
                return;
            }

            var result = await httpContext.AuthenticateAsync(authenticationScheme)
                .ConfigureAwait(false);
            if (result.Succeeded && result.Principal is not null)
            {
                var trustedPrincipal = CreateTrustedBearerPrincipal(result);
                if (trustedPrincipal is not null)
                {
                    httpContext.User = trustedPrincipal;
                    httpContext.Items[ValidatedPrincipalKey] = trustedPrincipal;
                    await next().ConfigureAwait(false);
                    return;
                }
            }

            // Defer the response to a second middleware placed inside the shared
            // audit and invalid-credential rate-limit layers, but still before
            // tenant resolution. No tenant or endpoint code can observe this request.
            httpContext.Items[AuthenticationFailureKey] = true;
            await next().ConfigureAwait(false);
        });
    }

    public static IApplicationBuilder UseMcpBearerAuthenticationRejection(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (httpContext, next) =>
        {
            if (!HasAuthenticationFailure(httpContext))
            {
                await next().ConfigureAwait(false);
                return;
            }

            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            McpProtectedResourceMetadataEndpointExtensions.StampChallengeOnUnauthorized(httpContext);
            await BuildInvalidTokenResult().ExecuteAsync(httpContext).ConfigureAwait(false);
        });
    }

    public static bool HasAuthenticationFailure(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(AuthenticationFailureKey, out var value) && value is true;
    }

    public static async ValueTask<object?> AuthenticateBearerAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;

        // Only engage when a bearer credential is actually presented. Absent an
        // Authorization header the anonymous handshake and the X-API-Key path are
        // untouched.
        if (!HasBearerCredential(httpContext))
        {
            return await next(context).ConfigureAwait(false);
        }

        // The application middleware already validated this exact principal before
        // tenant resolution. Tenant middleware stamps that same instance with the
        // canonical actor/effective tenant, so preserve it rather than authenticating
        // again and discarding framework-owned request-binding provenance.
        if (HasEarlyValidatedBearerPrincipal(httpContext))
        {
            return await next(context).ConfigureAwait(false);
        }

        var authenticationScheme = ResolveBearerAuthenticationScheme(httpContext);
        if (authenticationScheme is null)
        {
            return await next(context).ConfigureAwait(false);
        }

        var result = await httpContext
            .AuthenticateAsync(authenticationScheme)
            .ConfigureAwait(false);

        if (result.Succeeded && result.Principal is not null)
        {
            // Bind the validated principal to the request so the JSON-RPC handler and the
            // per-tool grant checks observe the same claim shape the X-API-Key path
            // produces.
            var trustedPrincipal = CreateTrustedBearerPrincipal(result);
            if (trustedPrincipal is not null)
            {
                httpContext.User = trustedPrincipal;
                return await next(context).ConfigureAwait(false);
            }
        }

        // A presented-but-invalid token is an RFC 6750 invalid_token rejection. Answer 401
        // with the MCP-structured error envelope; the RFC 9728 WWW-Authenticate challenge is
        // stamped by the response hook the transport arms on every MCP route.
        return BuildInvalidTokenResult();
    }

    internal static bool HasEarlyValidatedBearerPrincipal(HttpContext context) =>
        context.Items.TryGetValue(ValidatedPrincipalKey, out var principal)
        && ReferenceEquals(principal, context.User)
        && CanonicalSecurityActor.IsBearerPrincipal(context.User);

    private static bool HasBearerCredential(HttpContext context)
        => GetBearerCredential(context) is not null;

    private static string? GetBearerCredential(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization;
        var index = 0;
        while (index < authorization.Count)
        {
            var value = authorization[index];
            if (!string.IsNullOrEmpty(value) &&
                value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) &&
                value.Length > BearerPrefix.Length)
            {
                return value[BearerPrefix.Length..].Trim();
            }

            index++;
        }

        return null;
    }

    private static string? ResolveBearerAuthenticationScheme(HttpContext context)
    {
        var token = GetBearerCredential(context);
        var operatorBearer = context.RequestServices.GetService<OperatorBearerTokenService>();
        if (operatorBearer?.IsOperatorBearerCandidate(token) == true)
        {
            return OidcAuthenticationExtensions.OperatorBearerScheme;
        }

        var options = context.RequestServices
            .GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value;
        return McpProtectedResourceMetadata.ResolveAuthorizationServers(options).Count > 0
            ? OidcAuthenticationExtensions.CompositeScheme
            : null;
    }

    internal static bool IsMcpTransportPath(PathString path) =>
        path.Equals(McpEndpointExtensions.RoutePath, StringComparison.OrdinalIgnoreCase)
        || path.Equals($"{McpEndpointExtensions.RoutePath}/", StringComparison.OrdinalIgnoreCase);

    internal static ClaimsPrincipal? CreateTrustedBearerPrincipal(AuthenticateResult result)
    {
        var principal = result.Principal;
        if (principal is null)
        {
            return null;
        }

        // A policy scheme forwards to the concrete validator and preserves that
        // handler's ticket scheme. Only those framework-owned bearer schemes may
        // cross this authority boundary; no token claim can choose the scheme.
        // Policy-scheme authentication can retain the forwarding scheme on the ticket
        // ("Composite") even though the concrete JWT handler created the authenticated
        // identity as "Bearer". Use that framework-owned identity type as the concrete
        // provenance when the ticket itself is the forwarding wrapper.
        var sourceIdentity = principal.Identities.FirstOrDefault(static identity => identity.IsAuthenticated);
        var scheme = result.Ticket?.AuthenticationScheme;
        if (!string.Equals(scheme, OidcAuthenticationExtensions.JwtBearerScheme, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(scheme, OidcAuthenticationExtensions.OperatorBearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            scheme = sourceIdentity?.AuthenticationType;
        }
        if (!string.Equals(scheme, OidcAuthenticationExtensions.JwtBearerScheme, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(scheme, OidcAuthenticationExtensions.OperatorBearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var identity = new ClaimsIdentity(
            principal.Claims.Where(static claim =>
                !claim.Type.StartsWith("honua:", StringComparison.OrdinalIgnoreCase)
                || (claim.Type == OperatorScopeCatalog.ScopeGovernedClaimType
                    && CanonicalSecurityActor.IsFrameworkOwnedClaim(claim))),
            scheme,
            sourceIdentity?.NameClaimType ?? ClaimTypes.Name,
            sourceIdentity?.RoleClaimType ?? ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    private static IResult BuildInvalidTokenResult()
    {
        var response = new McpJsonRpcResponse
        {
            Id = McpEndpointExtensions.JsonNullId,
            Error = McpErrorMapper.InvalidToken()
        };

        return Results.Json(
            response,
            McpJsonContext.Default.McpJsonRpcResponse,
            statusCode: StatusCodes.Status401Unauthorized);
    }
}

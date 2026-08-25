// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using System.Security.Claims;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// OAuth 2.1 resource-server acceptance of <c>Authorization: Bearer</c> tokens on the
/// <c>/mcp</c> transport (honua-server#2850). Applied as an endpoint filter so a bearer
/// token is validated against the host's configured OIDC authorities and, on success,
/// projected onto <see cref="HttpContext.User"/> before the JSON-RPC handler runs — the
/// per-tool <c>EnsureCallerAuthorizedAsync</c> grant model then decides authorization on
/// the resulting principal exactly as it does for the X-API-Key path.
/// </summary>
/// <remarks>
/// <para>
/// The behaviour is strictly additive. It engages only when a request actually carries a
/// bearer credential; a request with no <c>Authorization</c> header (the anonymous
/// <c>initialize</c>/<c>tools/list</c> handshake) and a request already authenticated by
/// another scheme (X-API-Key) both flow through untouched. Coexistence policy between the
/// two credential families is a sibling concern (honua-server#2852) and is not decided
/// here.
/// </para>
/// <para>
/// Validation reuses the multi-authority OIDC stack rather than a parallel config surface:
/// the token is authenticated through the already-registered
/// <see cref="OidcAuthenticationExtensions.CompositeScheme"/>, which selects a
/// framework-owned bearer validator and enforces issuer, audience, signature, and lifetime
/// against every configured authority while normalizing claims through the shared
/// <c>OidcClaimsTransformation</c>. Acceptance is gated on the
/// same signal that drives the RFC 9728 metadata (honua-server#2849): when no authorization
/// server is configured there is nothing to validate against, so a presented token is left
/// unauthenticated and the request keeps its prior anonymous behaviour.
/// </para>
/// <para>
/// A token that is presented but fails validation — bad signature, expired, wrong issuer,
/// or an audience minted for another resource — is a resource-server rejection (RFC 6750
/// <c>invalid_token</c>): the filter short-circuits with HTTP 401 and an MCP-structured
/// error envelope. The 401 is paired with the RFC 9728 <c>WWW-Authenticate</c> challenge by
/// <see cref="McpProtectedResourceMetadataEndpointExtensions.StampChallengeOnUnauthorized"/>,
/// which the transport arms on every MCP route.
/// </para>
/// </remarks>
internal static class McpBearerAuthenticationEndpointExtensions
{
    private const string BearerPrefix = "Bearer ";
    private static readonly object AuthenticationFailureKey = new();
    private static readonly object ValidatedPrincipalKey = new();

    /// <summary>
    /// Validates MCP bearer credentials before tenant resolution. Endpoint filters
    /// remain as a defense-in-depth check, but cannot be the authority boundary.
    /// </summary>
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

            var options = httpContext.RequestServices
                .GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value;
            if (McpProtectedResourceMetadata.ResolveAuthorizationServers(options).Count == 0)
            {
                // No authority means no bearer identity. Anonymous discovery remains
                // available, while tool handlers still require an authenticated principal.
                await next().ConfigureAwait(false);
                return;
            }

            var result = await httpContext.AuthenticateAsync(OidcAuthenticationExtensions.CompositeScheme)
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

    /// <summary>
    /// Writes the fail-closed MCP invalid-token response after shared audit/rate
    /// middleware has entered, but before tenant resolution or endpoint execution.
    /// </summary>
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

    /// <summary>Whether early MCP bearer validation failed for this request.</summary>
    public static bool HasAuthenticationFailure(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(AuthenticationFailureKey, out var value) && value is true;
    }

    /// <summary>
    /// Endpoint filter that validates a presented bearer token and authenticates the caller
    /// against the configured OIDC authorities, or rejects an invalid token with a 401.
    /// </summary>
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

        // Resource-server validation is only meaningful when an authorization server is
        // configured. This is the same gate the RFC 9728 metadata uses (#2849): with no
        // authority there is nothing to validate the token against, so leave the request
        // anonymous and preserve prior behaviour. The JwtBearer scheme is registered
        // whenever this returns a non-empty set, so AuthenticateAsync below cannot fault on
        // a missing scheme.
        var options = httpContext.RequestServices
            .GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value;
        if (McpProtectedResourceMetadata.ResolveAuthorizationServers(options).Count == 0)
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

        var result = await httpContext
            .AuthenticateAsync(OidcAuthenticationExtensions.CompositeScheme)
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
                return true;
            }

            index++;
        }

        return false;
    }

    private static bool IsMcpTransportPath(PathString path) =>
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
        var scheme = result.Ticket?.AuthenticationScheme;
        if (!string.Equals(scheme, OidcAuthenticationExtensions.JwtBearerScheme, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(scheme, OidcAuthenticationExtensions.OperatorBearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var sourceIdentity = principal.Identities.FirstOrDefault(static identity => identity.IsAuthenticated);
        var identity = new ClaimsIdentity(
            principal.Claims.Where(static claim =>
                !claim.Type.StartsWith("honua:", StringComparison.OrdinalIgnoreCase)),
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

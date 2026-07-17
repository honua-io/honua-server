// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.Models;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

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
/// <see cref="OidcAuthenticationExtensions.JwtBearerScheme"/>, which enforces issuer,
/// audience, signature, and lifetime against every configured authority and normalizes the
/// claims through the shared <c>OidcClaimsTransformation</c>. Acceptance is gated on the
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

        // Already authenticated by an earlier scheme (for example the composite default
        // scheme validated the same bearer, or an X-API-Key principal is present): nothing
        // to add, and re-validating would be wasted work.
        if (httpContext.User.Identity?.IsAuthenticated == true)
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

        var result = await httpContext
            .AuthenticateAsync(OidcAuthenticationExtensions.JwtBearerScheme)
            .ConfigureAwait(false);

        if (result.Succeeded && result.Principal is not null)
        {
            // Bind the validated principal to the request so the JSON-RPC handler and the
            // per-tool grant checks observe the same claim shape the X-API-Key path
            // produces.
            httpContext.User = result.Principal;
            return await next(context).ConfigureAwait(false);
        }

        // A presented-but-invalid token is an RFC 6750 invalid_token rejection. Answer 401
        // with the MCP-structured error envelope; the RFC 9728 WWW-Authenticate challenge is
        // stamped by the response hook the transport arms on every MCP route.
        return BuildInvalidTokenResult();
    }

    private static bool HasBearerCredential(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization;
        foreach (var value in authorization)
        {
            if (!string.IsNullOrEmpty(value)
                && value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
                && value.Length > BearerPrefix.Length)
            {
                return true;
            }
        }

        return false;
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

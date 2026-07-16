// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Maps the RFC 9728 OAuth 2.0 Protected Resource Metadata document for the <c>/mcp</c>
/// resource and stamps the <c>WWW-Authenticate</c> challenge that points clients at it.
/// </summary>
/// <remarks>
/// <para>
/// The route is only mapped when the host has at least one valid OIDC authority
/// configured. With no authorization server there is nothing truthful to publish, so the
/// well-known location is <em>absent</em> (routing answers 404) rather than serving a
/// document with an empty <c>authorization_servers</c> array — an empty array would assert
/// "this resource is guarded by no authorization server", which is a different and false
/// claim (honua-server#2803).
/// </para>
/// <para>
/// The metadata is a discovery surface for an unauthenticated client and is therefore
/// anonymous, matching the <c>/mcp</c> route it describes.
/// </para>
/// </remarks>
internal static class McpProtectedResourceMetadataEndpointExtensions
{
    private const string ChallengeScheme = "Bearer";

    /// <summary>
    /// Maps <c>GET /.well-known/oauth-protected-resource/mcp</c> when an authorization
    /// server is configured; otherwise leaves the surface unmapped.
    /// </summary>
    public static IEndpointRouteBuilder MapMcpProtectedResourceMetadata(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value;
        var authorizationServers = McpProtectedResourceMetadata.ResolveAuthorizationServers(options);
        if (authorizationServers.Count == 0)
        {
            return endpoints;
        }

        endpoints.MapGet(McpProtectedResourceMetadata.RoutePath,
                (HttpContext context) => HandleGet(context, authorizationServers))
            .AllowAnonymous()
            .WithDisplayName("MCP Protected Resource Metadata")
            .WithName("McpProtectedResourceMetadata")
            .WithSummary("OAuth 2.0 Protected Resource Metadata (RFC 9728) for the /mcp resource.")
            .WithDescription("Publishes the resource identifier and the issuer identifiers of the authorization servers that guard POST /mcp, derived from the host's OIDC authority configuration. Mapped only when an authorization server is configured.")
            .WithTags("Mcp");

        return endpoints;
    }

    /// <summary>
    /// Appends the RFC 9728 section 5.1 <c>WWW-Authenticate</c> challenge carrying a
    /// <c>resource_metadata</c> parameter to any 401 the MCP surface produces. Registered as
    /// a response hook so the challenge tracks the final status code without the transport
    /// handlers having to know about OAuth.
    /// </summary>
    public static void StampChallengeOnUnauthorized(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(static state =>
        {
            var httpContext = (HttpContext)state;
            if (httpContext.Response.StatusCode == StatusCodes.Status401Unauthorized
                && TryBuildChallenge(httpContext, out var challenge))
            {
                httpContext.Response.Headers.Append("WWW-Authenticate", challenge);
            }

            return Task.CompletedTask;
        }, context);
    }

    /// <summary>
    /// Builds the <c>WWW-Authenticate</c> challenge value advertising this resource's
    /// metadata URL. Returns <see langword="false"/> when no authorization server is
    /// configured — there is then no metadata document to point at, so no challenge is
    /// emitted.
    /// </summary>
    internal static bool TryBuildChallenge(HttpContext context, out string challenge)
    {
        ArgumentNullException.ThrowIfNull(context);
        challenge = string.Empty;

        var options = context.RequestServices.GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value;
        if (McpProtectedResourceMetadata.ResolveAuthorizationServers(options).Count == 0)
        {
            return false;
        }

        if (!McpProtectedResourceMetadata.TryResolveResourceIdentifier(context, out var resource))
        {
            return false;
        }

        var metadataUrl = McpProtectedResourceMetadata.BuildMetadataUrl(resource);
        challenge = $"{ChallengeScheme} resource_metadata=\"{metadataUrl}\"";
        return true;
    }

    private static IResult HandleGet(HttpContext context, IReadOnlyList<string> authorizationServers)
    {
        if (!McpProtectedResourceMetadata.TryResolveResourceIdentifier(context, out var resource))
        {
            return Results.NotFound();
        }

        var document = new McpProtectedResourceMetadataDocument
        {
            Resource = resource.AbsoluteUri,
            AuthorizationServers = authorizationServers,
            ResourceName = McpProtectedResourceMetadata.ResourceName
        };

        return Results.Json(
            document,
            McpProtectedResourceMetadataJsonContext.Default.McpProtectedResourceMetadataDocument);
    }
}

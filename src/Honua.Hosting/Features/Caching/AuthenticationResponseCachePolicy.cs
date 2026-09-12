// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Prevents client caches from reusing authentication decisions or authenticated data.
/// </summary>
internal static class AuthenticationResponseCachePolicy
{
    // Grants an identity with no real credential (blocked outside the Test
    // environment); it carries no caller-specific data to leak, so it must not
    // trip the credentialed-response no-store rule below.
    private const string DevelopmentBypassAuthType = "dev-bypass";

    private static readonly object AuthenticationDecisionKey = new();

    internal static void PreventStorage(HttpContext context)
    {
        // Preserve the logical decision when a protocol uses HTTP 200 for an error.
        context.Items[AuthenticationDecisionKey] = true;
        context.Response.Headers.CacheControl = "no-store";
    }

    /// <summary>
    /// Signals that this request was authenticated entirely inside an endpoint handler
    /// (a static bearer/API-key check, for example) without attaching an
    /// <c>IsAuthenticated</c> identity to <see cref="HttpContext.User"/>. Endpoint-local
    /// authentication paths must call this so <see cref="Apply"/> still applies no-store
    /// to the success response.
    /// </summary>
    internal static void MarkAuthenticated(HttpContext context)
    {
        context.Items[AuthenticationDecisionKey] = true;
    }

    internal static void Apply(HttpContext context)
    {
        // Token exchanges authenticate credentials inside the endpoint and may
        // leave HttpContext.User anonymous even when the response contains a token.
        var credentialResponse = context.GetEndpoint()?.Metadata.GetMetadata<CredentialResponseCacheMetadata>() is not null;
        var isAuthenticationDecision = credentialResponse || context.Items.ContainsKey(AuthenticationDecisionKey) ||
            context.Response.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden;

        // An endpoint that already scoped its response to `private` with `Vary:
        // Authorization` (protected tiles/scene assets, terrain tiles) has already
        // ruled out shared/public caching and told downstream caches the response
        // varies by credential; that decision is authoritative and must survive
        // this catch-all. Only a missing Cache-Control, or one that is `private`
        // without the matching `Vary`, needs to be forced to no-store for a
        // credentialed response.
        if (isAuthenticationDecision ||
            (HasCredentialedIdentity(context) && !IsExplicitlyPrivate(context.Response)))
        {
            context.Response.Headers.CacheControl = "no-store";
        }

        if (credentialResponse)
        {
            // RFC 6749 section 5.1 also requires the legacy cache-prevention header.
            context.Response.Headers.Pragma = "no-cache";
        }
    }

    private static bool HasCredentialedIdentity(HttpContext context) =>
        context.User.Identities.Any(static identity =>
            identity.IsAuthenticated && identity.FindFirst("auth_type")?.Value != DevelopmentBypassAuthType);

    private static bool IsExplicitlyPrivate(HttpResponse response)
    {
        if (response.GetTypedHeaders().CacheControl is not { Private: true })
        {
            return false;
        }

        return response.Headers.Vary.Any(static value =>
            value is not null && value.Split(',').Any(static token =>
                token.Trim().Equals(HeaderNames.Authorization, StringComparison.OrdinalIgnoreCase)));
    }
}

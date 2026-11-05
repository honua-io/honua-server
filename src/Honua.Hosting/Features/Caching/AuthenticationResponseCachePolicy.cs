// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

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

    // An endpoint that authenticates inside the handler must call this or its success response can be stored.
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

        // Private caches can still reuse a fresh response after the server revokes
        // the credential: its bytes, Vary key and the asset's ETag have not changed.
        // Preserve an endpoint's private policy as metadata, but never allow it to
        // opt credentialed responses out of no-store (#4608). No-store takes
        // precedence over any retained freshness lifetime.
        if (isAuthenticationDecision || HasCredentialedIdentity(context))
        {
            var headers = context.Response.GetTypedHeaders();
            if (!isAuthenticationDecision && headers.CacheControl is { Private: true, Public: false } cacheControl)
            {
                cacheControl.NoStore = true;
                headers.CacheControl = cacheControl;
            }
            else
            {
                context.Response.Headers.CacheControl = "no-store";
            }
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
}

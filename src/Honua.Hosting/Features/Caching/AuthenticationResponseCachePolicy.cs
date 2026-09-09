// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Prevents client caches from reusing authentication decisions or authenticated data.
/// </summary>
internal static class AuthenticationResponseCachePolicy
{
    private static readonly object AuthenticationDecisionKey = new();

    internal static void PreventStorage(HttpContext context)
    {
        // Preserve the logical decision when a protocol uses HTTP 200 for an error.
        context.Items[AuthenticationDecisionKey] = true;
        context.Response.Headers.CacheControl = "no-store";
    }

    internal static void Apply(HttpContext context)
    {
        // Token exchanges authenticate credentials inside the endpoint and may
        // leave HttpContext.User anonymous even when the response contains a token.
        var credentialResponse = context.GetEndpoint()?.Metadata.GetMetadata<CredentialResponseCacheMetadata>() is not null;
        if (credentialResponse || context.Items.ContainsKey(AuthenticationDecisionKey) ||
            context.User.Identities.Any(static identity => identity.IsAuthenticated) ||
            context.Response.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
        {
            context.Response.Headers.CacheControl = "no-store";
        }

        if (credentialResponse)
        {
            // RFC 6749 section 5.1 also requires the legacy cache-prevention header.
            context.Response.Headers.Pragma = "no-cache";
        }
    }
}

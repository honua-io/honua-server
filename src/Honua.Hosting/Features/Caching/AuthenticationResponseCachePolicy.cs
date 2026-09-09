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
        if (context.Items.ContainsKey(AuthenticationDecisionKey) ||
            context.User.Identities.Any(static identity => identity.IsAuthenticated) ||
            context.Response.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
        {
            context.Response.Headers.CacheControl = "no-store";
        }
    }
}

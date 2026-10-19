// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.Authentication;

/// <summary>Explicit transport and rejection boundary for job-scoped SDK callbacks.</summary>
public static class ScopedJobAuthentication
{
    /// <summary>Authentication scheme for opaque custom-job capabilities.</summary>
    public const string Scheme = "ScopedJobToken";

    /// <summary>Job context required beside the bearer capability.</summary>
    public const string JobIdHeader = "X-Honua-Job-Id";

    internal const string FailureKey = "Honua.ScopedJobAuthentication.Failed";

    /// <summary>Whether the caller explicitly selected job-scoped authentication.</summary>
    public static bool HasCandidate(HttpContext context) => context.Request.Headers.ContainsKey(JobIdHeader);

    /// <summary>Whether an explicit candidate failed authentication.</summary>
    public static bool HasFailure(HttpContext context) => context.Items.TryGetValue(FailureKey, out var failure) && failure is true;

    /// <summary>Rejects invalid capabilities after shared audit and rate limiting have entered.</summary>
    public static IApplicationBuilder UseScopedJobAuthenticationRejection(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            if (!HasFailure(context))
            {
                await next().ConfigureAwait(false);
                return;
            }

            context.Response.Headers.WWWAuthenticate = "Bearer";
            await StandardErrorHelpers.CreateUnauthorized(context, "Invalid scoped job credential.").ExecuteAsync(context).ConfigureAwait(false);
        });
}

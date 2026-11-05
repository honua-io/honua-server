// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.Authentication;

public static class ScopedJobAuthentication
{
    public const string Scheme = "ScopedJobToken";

    public const string JobIdHeader = "X-Honua-Job-Id";

    internal const string FailureKey = "Honua.ScopedJobAuthentication.Failed";

    public static bool HasCandidate(HttpContext context) => context.Request.Headers.ContainsKey(JobIdHeader);

    public static bool HasFailure(HttpContext context) => context.Items.TryGetValue(FailureKey, out var failure) && failure is true;

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

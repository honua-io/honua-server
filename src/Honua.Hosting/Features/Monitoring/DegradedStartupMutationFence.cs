// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Health;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Refuses data-plane writes while a degraded start is still waiting on the database
/// (honua-server#6008). Readiness already answers 503, but FeatureServer applyEdits and
/// other mutations would still run. GET, HEAD, and <c>/healthz</c> stay available, and
/// this fence does not migrate.
/// </summary>
internal sealed class DegradedStartupMutationFenceMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var degraded = context.RequestServices.GetService<DegradedStartupContext>();
        if (degraded is { IsAwaitingDatabase: true }
            && IsMutating(context.Request.Method)
            && !IsHealthProbe(context.Request.Path))
        {
            var reasonCode = degraded.ReasonCode ?? ReadinessReasonCodes.DatabaseStartupDegraded;
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers[ReadinessReasonCodes.HeaderName] = reasonCode;
            return Task.CompletedTask;
        }

        return next(context);
    }

    private static bool IsMutating(string method)
        => HttpMethods.IsPost(method)
            || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method)
            || HttpMethods.IsDelete(method);

    private static bool IsHealthProbe(PathString path)
        => path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Extension methods for <see cref="DegradedStartupMutationFenceMiddleware"/>.</summary>
internal static class DegradedStartupMutationFenceExtensions
{
    /// <summary>
    /// Registers the degraded-start write fence. Place it as soon as the client HTTP method
    /// is visible so a mutation never reaches a handler that writes.
    /// </summary>
    public static IApplicationBuilder UseDegradedStartupMutationFence(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<DegradedStartupMutationFenceMiddleware>();
    }
}

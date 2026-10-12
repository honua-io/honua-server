// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Monitoring;

namespace Honua.ControlPlane;

/// <summary>
/// Refuses control-plane event work while the host is in a degraded start (honua-server#5916): the
/// database could not be reached at boot, or the schema is behind this build because the migration
/// task has not run yet. The event endpoints (the Lambda web-adapter <c>/events</c> route, the
/// batch-event and backstop routes, and the scheduled-tick route) answer a typed 503 so the
/// invocation fails and EventBridge or Scheduler retries it, instead of running a handler against a
/// database it cannot use.
/// </summary>
internal static class ControlPlaneNotReadyGuard
{
    /// <summary>Stable problem code of the not-ready answer.</summary>
    internal const string ProblemCode = "control_plane_not_ready";

    /// <summary>
    /// Returns the typed 503 to answer with while the host is degraded, or <see langword="null"/>
    /// when the host is ready to run control-plane work.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="degradedStartup">The degraded-start state, when composed.</param>
    /// <returns>The not-ready result, or <see langword="null"/>.</returns>
    public static IResult? TryRefuse(HttpContext context, DegradedStartupContext? degradedStartup)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (degradedStartup is not { IsAwaitingDatabase: true } degraded)
        {
            return null;
        }

        var reasonCode = degraded.ReasonCode ?? ReadinessReasonCodes.DatabaseStartupDegraded;
        context.Response.Headers[ReadinessReasonCodes.HeaderName] = reasonCode;
        return Results.Json(
            new ControlPlaneEventProblem(
                ProblemCode,
                $"The control-plane host is not ready ({reasonCode}); the event will be retried."),
            ControlPlaneEventJsonContext.Default.ControlPlaneEventProblem,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            contentType: "application/problem+json");
    }

    /// <summary>Resolves the degraded-start state from the request services.</summary>
    /// <param name="context">The current request.</param>
    /// <returns>The not-ready result, or <see langword="null"/>.</returns>
    public static IResult? TryRefuse(HttpContext context)
        => TryRefuse(context, context.RequestServices.GetService<DegradedStartupContext>());
}

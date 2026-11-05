// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Monitoring;

namespace Honua.ControlPlane;

// Event work is refused while the database was unreachable at boot or the schema is behind this build, so the invocation fails and can be retried.
internal static class ControlPlaneNotReadyGuard
{
    internal const string ProblemCode = "control_plane_not_ready";

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

    public static IResult? TryRefuse(HttpContext context)
        => TryRefuse(context, context.RequestServices.GetService<DegradedStartupContext>());
}

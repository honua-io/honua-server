// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Monitoring;

namespace Honua.ControlPlane;

// Event work is refused while the database was unreachable at boot or the schema is behind this build, so the invocation fails and can be retried.
internal static class ControlPlaneNotReadyGuard
{
    internal const string ProblemCode = "control_plane_not_ready";

    public static IResult? TryRefuse(
        HttpContext context,
        DegradedStartupContext? degradedStartup,
        MigrationState? migrationState = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (degradedStartup is { IsAwaitingDatabase: true } degraded)
        {
            return CreateNotReadyResult(context, degraded.ReasonCode ?? ReadinessReasonCodes.DatabaseStartupDegraded);
        }

        if (migrationState is { IsReady: false } migrations &&
            migrations.Status is MigrationLifecycleStatus.Running or MigrationLifecycleStatus.Failed)
        {
            var reasonCode = migrations.IsFailed
                ? ReadinessReasonCodes.MigrationsFailed
                : ReadinessReasonCodes.MigrationsInProgress;
            return CreateNotReadyResult(context, reasonCode);
        }

        return null;
    }

    public static IResult? TryRefuse(HttpContext context)
        => TryRefuse(
            context,
            context.RequestServices.GetService<DegradedStartupContext>(),
            context.RequestServices.GetService<MigrationState>());

    private static IResult CreateNotReadyResult(HttpContext context, string reasonCode)
    {
        context.Response.Headers[ReadinessReasonCodes.HeaderName] = reasonCode;
        return Results.Json(
            new ControlPlaneEventProblem(
                ProblemCode,
                $"The control-plane host is not ready ({reasonCode}); the event will be retried."),
            ControlPlaneEventJsonContext.Default.ControlPlaneEventProblem,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            contentType: "application/problem+json");
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Operations.Status;

internal static class OperateStatusEndpoints
{
    public static void MapOperateStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/operate")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Operate")
            .RequireOpsReadAuthorization();

        group.MapGet("/status", HandleGetStatus)
            .WithName("GetOperateStatus")
            .WithSummary("Get the server-authoritative aggregated operational status")
            .WithDescription(
                "Returns a server-computed overall verdict (healthy/degraded/unhealthy), per-domain "
                + "rollups (deploys, jobs, alerts, migrations, findings, telemetry backends), the platform "
                + "SLO source posture, and a separately named replica-local retained-tail diagnostic. "
                + "Accepts a read-only ops:read "
                + "credential as well as admin keys.")
            .Produces<OperateStatusResponse>();
    }

    private static async Task<IResult> HandleGetStatus(
        [FromServices] IOperateStatusService service,
        HttpContext context)
    {
        var status = await service.GetAsync(context.RequestAborted).ConfigureAwait(false);
        return Results.Json(status, OperateStatusJsonContext.Default.OperateStatusResponse);
    }
}

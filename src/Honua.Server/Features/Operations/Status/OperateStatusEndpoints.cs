// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Operations.Status;

/// <summary>
/// HTTP surface for the server-authoritative aggregated operational status (A12). One endpoint
/// returns a server-computed verdict plus per-domain rollups and an availability SLO snapshot, so a
/// copilot no longer stitches ~8 endpoints and invents its own health verdict. Guarded by the
/// read-only ops-reader policy: an <c>ops:read</c> key (or any admin key) can read it, but a mutating
/// ops operation still requires full admin write.
/// </summary>
internal static class OperateStatusEndpoints
{
    /// <summary>Maps the aggregated operate-status endpoint.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
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
                + "rollups (deploys, jobs, alerts, migrations, findings, telemetry backends), and — when "
                + "configured — an availability SLO / error-budget snapshot. Accepts a read-only ops:read "
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

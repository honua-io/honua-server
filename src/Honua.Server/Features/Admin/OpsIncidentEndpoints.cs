// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Monitoring;
using Npgsql;

namespace Honua.Server.Features.Admin;

internal static class OpsIncidentEndpoints
{
    public static void MapOpsIncidentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/incidents/{incidentId:guid}", HandleGetIncident)
            .WithDisplayName("Get Persisted Ops Incident Context")
            .RequireOpsReadAuthorization()
            .CacheOutput(policy => policy.NoCache())
            .Produces<OpsIncidentContextResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> HandleGetIncident(HttpContext context, Guid incidentId, int? limit = null)
    {
        if (limit is < 1 or > 1000)
        {
            return Problem(StatusCodes.Status400BadRequest, "Incident evidence limits must be between 1 and 1000.");
        }
        var reader = context.RequestServices.GetService<IOpsIncidentContextReader>();
        if (reader is null)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "Persisted incident context is unavailable.");
        }
        try
        {
            var query = new OpsIncidentContextQuery(new("platform", incidentId), limit ?? 100);
            var incident = await reader.ReadAsync(query, context.RequestAborted).ConfigureAwait(false);
            if (incident is null)
            {
                return Problem(StatusCodes.Status404NotFound, "The incident was not found.");
            }
            var response = new OpsIncidentContextResponse
            {
                Context = incident,
                ReadAt = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()
            };
            return Results.Json(response, OpsIncidentJsonContext.Default.OpsIncidentContextResponse);
        }
        catch (Exception exception) when (exception is NpgsqlException or IOException or TimeoutException)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "Persisted incident context is unavailable.");
        }
    }

    private static IResult Problem(int status, string detail) => ProblemDetailsHelpers.CreateAdminProblem(
        status, ProblemDetailsHelpers.GetTitle(status), detail);
}

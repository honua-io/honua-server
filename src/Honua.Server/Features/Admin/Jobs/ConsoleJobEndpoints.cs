// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin.Jobs;

internal static class ConsoleJobEndpoints
{
    public static void MapConsoleJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/jobs")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Jobs")
            .WithDescription("Console job runner observability")
            .RequireAdminAuthorization();

        group.MapGet("", HandleListJobs)
            .WithName("ListConsoleJobs")
            .WithSummary("List durable execution jobs")
            .Produces<ConsoleJobListResponse>();

        group.MapGet("/{jobId}", HandleGetJob)
            .WithName("GetConsoleJob")
            .WithSummary("Get durable execution job detail")
            .Produces<ConsoleJobDetail>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{jobId}/logs", HandleGetLogs)
            .WithName("GetConsoleJobLogs")
            .WithSummary("Get durable execution job logs")
            .Produces<ConsoleJobLogPageResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{jobId}/artifacts", HandleGetArtifacts)
            .WithName("GetConsoleJobArtifacts")
            .WithSummary("Get durable execution job artifacts")
            .Produces<ConsoleJobArtifactPageResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{jobId}/actions", HandleGetActions)
            .WithName("GetConsoleJobActions")
            .WithSummary("Get durable execution job control actions")
            .Produces<ConsoleJobActionsResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{jobId}/cancel", HandleCancel)
            .WithName("CancelConsoleJob")
            .WithSummary("Cancel a durable execution job")
            .Produces<ConsoleJobControlResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{jobId}/retry", HandleRetry)
            .WithName("RetryConsoleJob")
            .WithSummary("Retry a failed or cancelled durable execution job")
            .Produces<ConsoleJobControlResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> HandleListJobs(
        HttpContext context,
        [FromServices] IConsoleJobService service,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!AuthorizeRead(context, resourceId: null, out var authResult))
        {
            return authResult;
        }

        if (!TryParseQuery(context.Request.Query, out var query, out var error))
        {
            return BadRequest(error);
        }

        try
        {
            var response = await service.ListAsync(context, query, cancellationToken).ConfigureAwait(false);
            return Results.Json(response, ConsoleJobJsonContext.Default.ConsoleJobListResponse);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (ConsoleJobServiceUnavailableException ex)
        {
            return Unavailable(ex.Message);
        }
    }

    private static async Task<IResult> HandleGetJob(
        HttpContext context,
        string jobId,
        [FromServices] IConsoleJobService service,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!AuthorizeRead(context, jobId, out var authResult))
        {
            return authResult;
        }

        try
        {
            var response = await service.GetDetailAsync(context, jobId, cancellationToken).ConfigureAwait(false);
            if (response == null)
            {
                return NotFound(jobId);
            }

            SetCorrelationHeader(context, response.CorrelationId);
            return Results.Json(response, ConsoleJobJsonContext.Default.ConsoleJobDetail);
        }
        catch (ConsoleJobServiceUnavailableException ex)
        {
            return Unavailable(ex.Message);
        }
    }

    private static async Task<IResult> HandleGetLogs(
        HttpContext context,
        string jobId,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        [FromServices] IConsoleJobService service,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!AuthorizeRead(context, jobId, out var authResult))
        {
            return authResult;
        }

        try
        {
            var response = await service.GetLogsAsync(
                    context,
                    jobId,
                    cursor,
                    limit ?? 100,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response == null)
            {
                return NotFound(jobId);
            }

            SetCorrelationHeader(context, response.CorrelationId);
            return Results.Json(response, ConsoleJobJsonContext.Default.ConsoleJobLogPageResponse);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (ConsoleJobServiceUnavailableException ex)
        {
            return Unavailable(ex.Message);
        }
    }

    private static async Task<IResult> HandleGetArtifacts(
        HttpContext context,
        string jobId,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        [FromServices] IConsoleJobService service,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!AuthorizeRead(context, jobId, out var authResult))
        {
            return authResult;
        }

        try
        {
            var response = await service.GetArtifactsAsync(
                    context,
                    jobId,
                    cursor,
                    limit ?? 100,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response == null)
            {
                return NotFound(jobId);
            }

            SetCorrelationHeader(context, response.CorrelationId);
            return Results.Json(response, ConsoleJobJsonContext.Default.ConsoleJobArtifactPageResponse);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (ConsoleJobServiceUnavailableException ex)
        {
            return Unavailable(ex.Message);
        }
    }

    private static async Task<IResult> HandleGetActions(
        HttpContext context,
        string jobId,
        [FromServices] IConsoleJobService service,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!AuthorizeRead(context, jobId, out var authResult))
        {
            return authResult;
        }

        try
        {
            var response = await service.GetActionsAsync(context, jobId, cancellationToken).ConfigureAwait(false);
            if (response == null)
            {
                return NotFound(jobId);
            }

            SetCorrelationHeader(context, response.CorrelationId);
            return Results.Json(response, ConsoleJobJsonContext.Default.ConsoleJobActionsResponse);
        }
        catch (ConsoleJobServiceUnavailableException ex)
        {
            return Unavailable(ex.Message);
        }
    }

    private static async Task<IResult> HandleCancel(
        HttpContext context,
        string jobId,
        [FromServices] IConsoleJobService service,
        CancellationToken cancellationToken)
    {
        return await HandleControl(context, jobId, service.CancelAsync, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleRetry(
        HttpContext context,
        string jobId,
        [FromServices] IConsoleJobService service,
        CancellationToken cancellationToken)
    {
        return await HandleControl(context, jobId, service.RetryAsync, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleControl(
        HttpContext context,
        string jobId,
        Func<HttpContext, string, CancellationToken, Task<ConsoleJobControlResponse?>> action,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!AuthorizeExecute(context, jobId, out var authResult))
        {
            return authResult;
        }

        try
        {
            var response = await action(context, jobId, cancellationToken).ConfigureAwait(false);
            if (response == null)
            {
                return NotFound(jobId);
            }

            SetCorrelationHeader(context, response.CorrelationId);
            return Results.Json(response, ConsoleJobJsonContext.Default.ConsoleJobControlResponse);
        }
        catch (ConsoleJobApprovalRequiredException ex)
        {
            return ex.Result;
        }
        catch (ConsoleJobConflictException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status409Conflict,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                ex.Message);
        }
        catch (ConsoleJobNotFoundException)
        {
            return NotFound(jobId);
        }
        catch (ConsoleJobServiceUnavailableException ex)
        {
            return Unavailable(ex.Message);
        }
    }

    private static bool TryParseQuery(IQueryCollection query, out ConsoleJobQueryRequest request, out string error)
    {
        request = new ConsoleJobQueryRequest();
        error = string.Empty;

        if (!QueryFilterParsers.TryParseEnumList<ExecutionJobStatus>(query, "status", out var statuses, out var parseError) ||
            !QueryFilterParsers.TryParseDateTimeOffset(query, "from", out var from, out parseError) ||
            !QueryFilterParsers.TryParseDateTimeOffset(query, "to", out var to, out parseError) ||
            !QueryFilterParsers.TryParseInt(query, "limit", out var limit, out parseError))
        {
            error = parseError;
            return false;
        }

        ExecutionJobKind? kind = null;
        var rawKind = QueryFilterParsers.GetString(query, "kind");
        if (!string.IsNullOrWhiteSpace(rawKind))
        {
            if (!QueryFilterParsers.TryParseDefinedEnum<ExecutionJobKind>(rawKind, out var parsedKind))
            {
                error = $"'kind' contains unsupported value '{rawKind}'.";
                return false;
            }

            kind = parsedKind;
        }

        request = new ConsoleJobQueryRequest
        {
            Statuses = statuses ?? Array.Empty<ExecutionJobStatus>(),
            Kind = kind,
            Backend = QueryFilterParsers.GetString(query, "backend"),
            Queue = QueryFilterParsers.GetString(query, "queue"),
            RequestedBy = QueryFilterParsers.GetString(query, "actor")
                ?? QueryFilterParsers.GetString(query, "requestedBy"),
            CorrelationId = QueryFilterParsers.GetString(query, "correlationId"),
            TraceId = QueryFilterParsers.GetString(query, "traceId"),
            DefinitionId = QueryFilterParsers.GetString(query, "definitionId"),
            ResourceRef = QueryFilterParsers.GetString(query, "resourceRef"),
            Environment = QueryFilterParsers.GetString(query, "environment"),
            Server = QueryFilterParsers.GetString(query, "server"),
            ReleaseId = QueryFilterParsers.GetString(query, "releaseId"),
            ChangeSetId = QueryFilterParsers.GetString(query, "changeSetId"),
            AlertId = QueryFilterParsers.GetString(query, "alertId"),
            CreatedFrom = from,
            CreatedTo = to,
            Cursor = QueryFilterParsers.GetString(query, "cursor"),
            Limit = limit ?? 50
        };

        return true;
    }

    private static bool AuthorizeRead(HttpContext context, string? resourceId, out IResult result)
    {
        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var denied = gate.EvaluateAuthorization(
            context,
            OperatorResourceType.Job,
            OperatorOperation.Read,
            resourceId);
        result = denied ?? Results.Empty;
        return denied == null;
    }

    private static bool AuthorizeExecute(HttpContext context, string resourceId, out IResult result)
    {
        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var denied = gate.EvaluateAuthorization(
            context,
            OperatorResourceType.Job,
            OperatorOperation.Execute,
            resourceId);
        result = denied ?? Results.Empty;
        return denied == null;
    }

    private static IResult BadRequest(string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(
            StatusCodes.Status400BadRequest,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
            detail);

    private static IResult NotFound(string jobId)
        => ProblemDetailsHelpers.CreateAdminProblem(
            StatusCodes.Status404NotFound,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
            $"Job '{jobId}' was not found.");

    private static IResult Unavailable(string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(
            StatusCodes.Status503ServiceUnavailable,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
            detail);

    private static void SetNoStore(HttpContext context)
        => context.Response.Headers.CacheControl = "no-store";

    private static void SetCorrelationHeader(HttpContext context, string? correlationId)
    {
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            context.Response.Headers["X-Correlation-Id"] = correlationId;
        }
    }
}

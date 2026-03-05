// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Progress;

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// Admin endpoints for asynchronous tile cache lifecycle operations.
/// </summary>
internal static class TileOperationsEndpoints
{
    public static void MapTileOperationsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/tile-operations")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Tile Operations")
            .WithDescription("Tile cache seed/warm/invalidate/purge and PMTiles export job control")
            .RequireAdminAuthorization();

        _ = group.Map("/jobs", HandleStartJob)
            .WithName("StartTileOperationJob")
            .WithSummary("Start a tile operation job")
            .WithDescription("Queues a tile operation (seed, warm, invalidate, purge, export_pmtiles) for asynchronous execution")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));

        _ = group.Map("/jobs/{jobId}", HandleGetJobStatus)
            .WithName("GetTileOperationJobStatus")
            .WithSummary("Get tile operation job status")
            .WithDescription("Returns progress and status details for a tile operation job")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]));

        _ = group.Map("/jobs", HandleListJobs)
            .WithName("ListTileOperationJobs")
            .WithSummary("List tile operation jobs")
            .WithDescription("Lists tile operation jobs, optionally including completed jobs")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]));

        _ = group.Map("/jobs/{jobId}/cancel", HandleCancelJob)
            .WithName("CancelTileOperationJob")
            .WithSummary("Cancel tile operation job")
            .WithDescription("Attempts to cancel a queued or running tile operation job")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));

        _ = group.Map("/jobs/{jobId}/retry", HandleRetryJob)
            .WithName("RetryTileOperationJob")
            .WithSummary("Retry tile operation job")
            .WithDescription("Queues a retry of a failed or cancelled tile operation job")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));
    }

    private static async Task<IResult> HandleStartJob(
        TileOperationStartRequest request,
        HttpContext context,
        ITileOperationJobService jobService,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateStartRequest(request);
        if (validationError != null)
        {
            return validationError;
        }

        try
        {
            var jobId = await jobService.StartAsync(request, cancellationToken).ConfigureAwait(false);
            var response = new TileOperationStartResponse
            {
                JobId = jobId,
                Message = "Tile operation queued.",
                StatusUrl = $"/api/v1/admin/tile-operations/jobs/{jobId}",
                CancelUrl = $"/api/v1/admin/tile-operations/jobs/{jobId}/cancel"
            };

            return Results.Json(
                response,
                TileOperationsJsonContext.Default.TileOperationStartResponse,
                statusCode: StatusCodes.Status202Accepted);
        }
        catch (ArgumentException ex)
        {
            return StandardErrorHelpers.CreateBadRequest(context, ex.Message);
        }
    }

    private static async Task<IResult> HandleGetJobStatus(
        string jobId,
        HttpContext context,
        ITileOperationJobService jobService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Job ID is required.");
        }

        var progress = await jobService.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (progress == null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile job '{jobId}' not found.");
        }

        return Results.Json(progress, TileOperationsJsonContext.Default.TileOperationProgress);
    }

    private static async Task<IResult> HandleListJobs(
        ITileOperationJobService jobService,
        bool activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        var jobs = await jobService.ListAsync(activeOnly, cancellationToken).ConfigureAwait(false);
        var response = new TileOperationListResponse
        {
            Jobs = jobs.ToArray(),
            TotalCount = jobs.Count
        };

        return Results.Json(response, TileOperationsJsonContext.Default.TileOperationListResponse);
    }

    private static async Task<IResult> HandleCancelJob(
        string jobId,
        HttpContext context,
        ITileOperationJobService jobService,
        CancellationToken cancellationToken)
    {
        var progress = await jobService.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (progress == null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile job '{jobId}' not found.");
        }

        var cancelled = await jobService.CancelAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!cancelled)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Tile job cannot be cancelled in its current state.");
        }

        var response = new TileOperationCancelResponse
        {
            JobId = jobId,
            Message = "Cancellation requested."
        };
        return Results.Json(response, TileOperationsJsonContext.Default.TileOperationCancelResponse);
    }

    private static async Task<IResult> HandleRetryJob(
        string jobId,
        HttpContext context,
        ITileOperationJobService jobService,
        CancellationToken cancellationToken)
    {
        var retryJobId = await jobService.RetryAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (retryJobId == null)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Tile job cannot be retried. Ensure the job exists and is in failed/cancelled state.");
        }

        var response = new TileOperationRetryResponse
        {
            OriginalJobId = jobId,
            RetryJobId = retryJobId,
            StatusUrl = $"/api/v1/admin/tile-operations/jobs/{retryJobId}"
        };
        return Results.Json(
            response,
            TileOperationsJsonContext.Default.TileOperationRetryResponse,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static IResult? ValidateStartRequest(TileOperationStartRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Operation))
        {
            return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Property 'operation' is required."));
        }

        if (request.Bbox != null && request.Bbox.Length != 4)
        {
            return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Property 'bbox' must contain exactly four values: [minLon, minLat, maxLon, maxLat]."));
        }

        if (request.MinZoom.HasValue && request.MaxZoom.HasValue && request.MinZoom > request.MaxZoom)
        {
            return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Property 'minZoom' must be less than or equal to 'maxZoom'."));
        }

        var operation = request.Operation.Trim().ToLowerInvariant();
        if (operation is "seed" or "warm")
        {
            if (!request.LayerId.HasValue && string.IsNullOrWhiteSpace(request.ServiceId))
            {
                return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status400BadRequest,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                    "Seed/warm operations require either 'layerId' or 'serviceId'."));
            }
        }

        if (operation == "export_pmtiles" && !request.LayerId.HasValue)
        {
            return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "PMTiles export operation requires 'layerId'."));
        }

        return null;
    }
}

internal sealed record TileOperationStartResponse
{
    public required string JobId { get; init; }
    public required string Message { get; init; }
    public required string StatusUrl { get; init; }
    public required string CancelUrl { get; init; }
}

internal sealed record TileOperationCancelResponse
{
    public required string JobId { get; init; }
    public required string Message { get; init; }
}

internal sealed record TileOperationRetryResponse
{
    public required string OriginalJobId { get; init; }
    public required string RetryJobId { get; init; }
    public required string StatusUrl { get; init; }
}

internal sealed record TileOperationListResponse
{
    public required TileOperationProgress[] Jobs { get; init; }
    public required int TotalCount { get; init; }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.General)]
[System.Text.Json.Serialization.JsonSerializable(typeof(TileOperationStartRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TileOperationProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TileOperationProgress[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TileOperationStartResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TileOperationCancelResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TileOperationRetryResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TileOperationListResponse))]
internal sealed partial class TileOperationsJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

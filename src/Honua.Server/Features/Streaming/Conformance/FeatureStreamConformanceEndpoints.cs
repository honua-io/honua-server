// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Streaming.Conformance;

internal static class FeatureStreamConformanceEndpoints
{
    public const string RunTokenHeader = "X-Honua-Conformance-Run-Token";

    public static void MapFeatureStreamConformanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/streaming/conformance")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Streaming", "Conformance");

        group.MapPost("/runs", HandleLeaseRun)
            .WithDisplayName("Lease Controlled Conformance Run")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .WithDescription("Acquires an isolated conformance run lease bound to this deployment's immutable revision and dedicated conformance source.")
            .Produces<ApiResponse<FeatureStreamConformanceRunResponse>>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireConformanceMutateAuthorization();

        group.MapPost("/runs/{runId}/mutations", HandleMutate)
            .WithDisplayName("Apply Controlled Conformance Mutation")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .WithDescription("Applies one bounded, ownership-checked mutation through the canonical edit pipeline so it is observable on every advertised stream transport.")
            .Produces<ApiResponse<FeatureStreamConformanceMutationResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireConformanceMutateAuthorization();

        group.MapDelete("/runs/{runId}", HandleCleanupRun)
            .WithDisplayName("Release Controlled Conformance Run")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Delete]))
            .WithDescription("Releases a conformance run and deletes every record it owns. Idempotent, so it is safe to call from a finally block.")
            .Produces<ApiResponse<FeatureStreamConformanceCleanupResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireConformanceMutateAuthorization();

        var adminGroup = endpoints.MapGroup("/api/v{version:apiVersion}/admin/streaming/conformance")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Streaming", "Conformance")
            .RequireAdminAuthorization();

        adminGroup.MapPost("/reset", HandleReset)
            .WithDisplayName("Reset Controlled Conformance Source")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .WithDescription("Drops every conformance lease and deletes every controlled record, returning the conformance source to its immutable baseline.")
            .Produces<ApiResponse<FeatureStreamConformanceResetResponse>>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    [Honua.Infrastructure.RateLimiting.RateLimit(30)]
    private static async Task<IResult> HandleLeaseRun(
        [FromServices] FeatureStreamConformanceService service,
        HttpContext context)
    {
        var request = await ReadBodyAsync(
            context,
            FeatureStreamConformanceJsonContext.Default.FeatureStreamConformanceRunRequest).ConfigureAwait(false)
            ?? new FeatureStreamConformanceRunRequest();

        var result = await service.LeaseRunAsync(request, context.RequestAborted).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return ToProblem(context, result.Failure, result.Message);
        }

        return Results.Json(
            ApiResponse<FeatureStreamConformanceRunResponse>.CreateSuccess(result.Value!),
            FeatureStreamConformanceJsonContext.Default.ApiResponseFeatureStreamConformanceRunResponse,
            statusCode: StatusCodes.Status201Created);
    }

    [Honua.Infrastructure.RateLimiting.RateLimit(120)]
    private static async Task<IResult> HandleMutate(
        string runId,
        [FromServices] FeatureStreamConformanceService service,
        HttpContext context)
    {
        if (!TryParseRunId(runId, out var parsedRunId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                "No live conformance run matches that identifier and token.");
        }

        var request = await ReadBodyAsync(
            context,
            FeatureStreamConformanceJsonContext.Default.FeatureStreamConformanceMutationRequest).ConfigureAwait(false);
        if (request is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "A JSON body naming the conformance operation is required.");
        }

        var result = await service.MutateAsync(
            context,
            parsedRunId,
            ResolveRunToken(context),
            request,
            context.RequestAborted).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return ToProblem(context, result.Failure, result.Message);
        }

        return Results.Json(
            ApiResponse<FeatureStreamConformanceMutationResponse>.CreateSuccess(result.Value!),
            FeatureStreamConformanceJsonContext.Default.ApiResponseFeatureStreamConformanceMutationResponse);
    }

    [Honua.Infrastructure.RateLimiting.RateLimit(60)]
    private static async Task<IResult> HandleCleanupRun(
        string runId,
        [FromServices] FeatureStreamConformanceService service,
        HttpContext context)
    {
        if (!TryParseRunId(runId, out var parsedRunId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                "No live conformance run matches that identifier and token.");
        }

        var result = await service.CleanupRunAsync(
            context,
            parsedRunId,
            ResolveRunToken(context),
            context.RequestAborted).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return ToProblem(context, result.Failure, result.Message);
        }

        return Results.Json(
            ApiResponse<FeatureStreamConformanceCleanupResponse>.CreateSuccess(result.Value!),
            FeatureStreamConformanceJsonContext.Default.ApiResponseFeatureStreamConformanceCleanupResponse);
    }

    private static async Task<IResult> HandleReset(
        [FromServices] FeatureStreamConformanceService service,
        HttpContext context)
    {
        var result = await service.ResetAsync(context, context.RequestAborted).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Json(
                ApiResponse<FeatureStreamConformanceResetResponse>.CreateSuccess(result.Value!),
                FeatureStreamConformanceJsonContext.Default.ApiResponseFeatureStreamConformanceResetResponse)
            : ToProblem(context, result.Failure, result.Message);
    }

    // Unknown-run and foreign-record both answer 404 so the surface cannot confirm another run.
    private static IResult ToProblem(HttpContext context, FeatureStreamConformanceFailure failure, string? message)
    {
        var detail = message ?? "The controlled-conformance request was refused.";
        return failure switch
        {
            FeatureStreamConformanceFailure.Disabled =>
                ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status403Forbidden, detail),
            FeatureStreamConformanceFailure.SourceUnavailable or
            FeatureStreamConformanceFailure.DeploymentRevisionUnavailable =>
                ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status503ServiceUnavailable, detail),
            FeatureStreamConformanceFailure.DeploymentRevisionMismatch or
            FeatureStreamConformanceFailure.SourceIdentityMismatch or
            FeatureStreamConformanceFailure.LeaseUnavailable or
            FeatureStreamConformanceFailure.MutationBudgetExhausted or
            FeatureStreamConformanceFailure.RecordBudgetExhausted =>
                ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict, detail),
            FeatureStreamConformanceFailure.RunNotFound or
            FeatureStreamConformanceFailure.RecordNotOwned =>
                ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, detail),
            _ => StandardErrorHelpers.CreateBadRequest(context, detail)
        };
    }

    private static string? ResolveRunToken(HttpContext context)
        => context.Request.Headers.TryGetValue(RunTokenHeader, out var values)
            ? values.ToString()
            : null;

    private static bool TryParseRunId(string? value, out Guid runId)
        => Guid.TryParseExact(value, "N", out runId) || Guid.TryParse(value, out runId);

    private static async Task<T?> ReadBodyAsync<T>(
        HttpContext context,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                typeInfo,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

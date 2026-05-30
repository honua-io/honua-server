// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Geoprocessing;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.AnalysisContent;

internal static partial class AnalysisContentEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisContentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var content = endpoints.MapGroup("/api/v{version:apiVersion}/analysis/content")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Analysis", "Content")
            .WithDescription("Durable saved-query and analysis-package content versions.")
            .RequireAdminAuthorization();

        _ = content.MapPost("/items", HandleCreateItemAsync)
            .WithName("CreateAnalysisContentItem")
            .WithSummary("Create a saved-query or analysis-package content item.");

        _ = content.MapGet("/items/{itemId}", HandleGetItemAsync)
            .WithName("GetAnalysisContentItem")
            .WithSummary("Open an analysis content item at its latest version.");

        _ = content.MapGet("/items/{itemId}/versions/latest", HandleGetLatestVersionAsync)
            .WithName("GetLatestAnalysisContentVersion")
            .WithSummary("Open the latest immutable analysis content version.");

        _ = content.MapGet("/items/{itemId}/versions/{contentVersion:int}", HandleGetVersionAsync)
            .WithName("GetAnalysisContentVersion")
            .WithSummary("Open an explicit immutable analysis content version.");

        _ = content.MapPost("/items/{itemId}/versions", HandleCreateVersionAsync)
            .WithName("CreateAnalysisContentVersion")
            .WithSummary("Create a new immutable analysis content version.");

        _ = content.MapPost("/items/{itemId}/versions/{contentVersion:int}/preview", HandlePreviewAsync)
            .WithName("PreviewSavedQueryVersion")
            .WithSummary("Preview a saved-query version through the canonical feature-query pipeline.");

        _ = content.MapPost("/items/{itemId}/versions/{contentVersion:int}/runs", HandleRunAsync)
            .WithName("RunAnalysisPackageVersion")
            .WithSummary("Submit an analysis-package version to the canonical geoprocessing runtime.");

        _ = content.MapPost("/items/{itemId}/versions/{contentVersion:int}/reruns", HandleRerunAsync)
            .WithName("RerunAnalysisPackageVersion")
            .WithSummary("Rerun an analysis-package version with provenance links.");

        var artifacts = endpoints.MapGroup("/api/v{version:apiVersion}/analysis/artifacts")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Analysis", "Artifacts")
            .RequireAdminAuthorization();

        _ = artifacts.MapGet("/{artifactId}", HandleGetArtifactAsync)
            .WithName("GetAnalysisArtifact")
            .WithSummary("Resolve stable artifact metadata for downstream bindings.");

        var jobs = endpoints.MapGroup("/api/v{version:apiVersion}/analysis/jobs")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Analysis", "Jobs")
            .RequireAdminAuthorization();

        _ = jobs.MapGet("/{jobId}/logs", HandleGetJobLogsAsync)
            .WithName("GetAnalysisJobLogs")
            .WithSummary("Read bounded safe structured logs for an analysis job.");

        _ = jobs.MapGet("/{jobId}/failure", HandleGetJobFailureAsync)
            .WithName("GetAnalysisJobFailure")
            .WithSummary("Read safe failure classification for a failed analysis job.");

        return endpoints;
    }

    private static async Task<IResult> HandleCreateItemAsync(
        CreateAnalysisContentItemRequest request,
        [FromServices] IAnalysisContentService service,
        HttpContext context)
    {
        try
        {
            var result = await service.CreateItemAsync(
                new CreateAnalysisContentItemCommand(
                    request.Kind,
                    request.Name,
                    request.Title,
                    request.SavedQuery,
                    request.AnalysisPackage),
                context.User,
                context.RequestAborted).ConfigureAwait(false);

            var response = ToItemResponse(result);
            return Results.Json(
                response,
                AnalysisContentApiJsonContext.Default.AnalysisContentItemResponse,
                statusCode: StatusCodes.Status201Created);
        }
        catch (Exception ex) when (TryMapException(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "analysis-content.create", ex);
            return CreateGenericProblem(context);
        }
    }

    private static async Task<IResult> HandleGetItemAsync(
        string itemId,
        [FromServices] IAnalysisContentService service,
        HttpContext context)
    {
        try
        {
            var result = await service.GetItemAsync(itemId, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(ToItemResponse(result), AnalysisContentApiJsonContext.Default.AnalysisContentItemResponse);
        }
        catch (Exception ex) when (TryMapException(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "analysis-content.get", ex);
            return CreateGenericProblem(context);
        }
    }

    private static Task<IResult> HandleGetLatestVersionAsync(
        string itemId,
        [FromServices] IAnalysisContentService service,
        HttpContext context)
        => HandleGetVersionCoreAsync(itemId, null, service, context);

    private static Task<IResult> HandleGetVersionAsync(
        string itemId,
        int contentVersion,
        [FromServices] IAnalysisContentService service,
        HttpContext context)
        => HandleGetVersionCoreAsync(itemId, contentVersion, service, context);

    private static async Task<IResult> HandleGetVersionCoreAsync(
        string itemId,
        int? contentVersion,
        IAnalysisContentService service,
        HttpContext context)
    {
        try
        {
            var result = await service.GetVersionAsync(
                itemId,
                contentVersion,
                context.RequestAborted).ConfigureAwait(false);
            return Results.Json(
                ToVersionResponse(result),
                AnalysisContentApiJsonContext.Default.AnalysisContentVersionResponse);
        }
        catch (Exception ex) when (TryMapException(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "analysis-content.version.get", ex);
            return CreateGenericProblem(context);
        }
    }

    private static async Task<IResult> HandleCreateVersionAsync(
        string itemId,
        CreateAnalysisContentVersionRequest request,
        [FromServices] IAnalysisContentService service,
        HttpContext context)
    {
        try
        {
            var result = await service.AddVersionAsync(
                itemId,
                new CreateAnalysisContentVersionCommand(
                    request.SavedQuery,
                    request.AnalysisPackage,
                    request.BasedOnVersionId,
                    request.CreatedFromJobId,
                    request.CreatedFromArtifactIds),
                context.User,
                context.RequestAborted).ConfigureAwait(false);

            return Results.Json(
                ToVersionResponse(result),
                AnalysisContentApiJsonContext.Default.AnalysisContentVersionResponse,
                statusCode: StatusCodes.Status201Created);
        }
        catch (Exception ex) when (TryMapException(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "analysis-content.version.create", ex);
            return CreateGenericProblem(context);
        }
    }

    private static async Task<IResult> HandlePreviewAsync(
        string itemId,
        int contentVersion,
        PreviewSavedQueryRequest request,
        [FromServices] IAnalysisContentService service,
        HttpContext context)
    {
        try
        {
            var result = await service.PreviewSavedQueryAsync(
                itemId,
                contentVersion,
                request.Limit,
                context.RequestAborted).ConfigureAwait(false);
            return Results.Json(result, AnalysisContentApiJsonContext.Default.SavedQueryPreviewResult);
        }
        catch (Exception ex) when (TryMapException(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "analysis-content.preview", ex);
            return CreateGenericProblem(context);
        }
    }

    private static async Task<IResult> HandleRunAsync(
        string itemId,
        int contentVersion,
        RunAnalysisContentVersionRequest request,
        [FromServices] IAnalysisContentService service,
        HttpContext context)
    {
        try
        {
            var result = await service.SubmitAnalysisPackageAsync(
                itemId,
                contentVersion,
                new RunAnalysisContentVersionCommand(request.IdempotencyKey, request.Parameters),
                context.User,
                context.RequestAborted).ConfigureAwait(false);
            return Results.Json(ToJobResponse(result), AnalysisContentApiJsonContext.Default.AnalysisContentJobResponse);
        }
        catch (Exception ex) when (TryMapException(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "analysis-content.run", ex);
            return CreateGenericProblem(context);
        }
    }

    private static async Task<IResult> HandleRerunAsync(
        string itemId,
        int contentVersion,
        RerunAnalysisContentVersionRequest request,
        [FromServices] IAnalysisContentService service,
        HttpContext context)
    {
        try
        {
            var result = await service.RerunAnalysisPackageAsync(
                itemId,
                contentVersion,
                new RerunAnalysisContentVersionCommand(
                    request.IdempotencyKey,
                    request.RerunOfJobId,
                    request.RerunOfResultPackageId,
                    request.ParameterOverrides),
                context.User,
                context.RequestAborted).ConfigureAwait(false);
            return Results.Json(ToJobResponse(result), AnalysisContentApiJsonContext.Default.AnalysisContentJobResponse);
        }
        catch (Exception ex) when (TryMapException(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "analysis-content.rerun", ex);
            return CreateGenericProblem(context);
        }
    }

    private static async Task<IResult> HandleGetArtifactAsync(
        string artifactId,
        [FromServices] IAnalysisContentService service,
        HttpContext context)
    {
        try
        {
            var artifact = await service.GetArtifactAsync(artifactId, context.RequestAborted).ConfigureAwait(false);
            var binding = new ArtifactBindingRef
            {
                ArtifactId = artifact.ArtifactId,
                SourceItemId = artifact.SourceItemId,
                SourceVersion = artifact.SourceVersion,
                SourceVersionId = artifact.SourceVersionId,
                Role = "dataSource",
                TargetKind = "content",
                TargetSlot = "source"
            };
            return Results.Json(
                new AnalysisArtifactResponse { Artifact = artifact, Binding = binding },
                AnalysisContentApiJsonContext.Default.AnalysisArtifactResponse);
        }
        catch (Exception ex) when (TryMapException(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "analysis-content.artifact.get", ex);
            return CreateGenericProblem(context);
        }
    }

    private static async Task<IResult> HandleGetJobLogsAsync(
        string jobId,
        [FromQuery] int? limit,
        [FromServices] IAnalysisContentService service,
        HttpContext context)
    {
        try
        {
            var logs = await service.GetJobLogsAsync(jobId, limit, context.User, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(logs, AnalysisContentApiJsonContext.Default.AnalysisJobLogs);
        }
        catch (Exception ex) when (TryMapException(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "analysis-content.job.logs", ex);
            return CreateGenericProblem(context);
        }
    }

    private static async Task<IResult> HandleGetJobFailureAsync(
        string jobId,
        [FromServices] IAnalysisContentService service,
        HttpContext context)
    {
        try
        {
            var failure = await service.GetJobFailureAsync(
                jobId,
                context.User,
                context.RequestAborted).ConfigureAwait(false);
            return Results.Json(failure, AnalysisContentApiJsonContext.Default.AnalysisJobFailure);
        }
        catch (Exception ex) when (TryMapException(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "analysis-content.job.failure", ex);
            return CreateGenericProblem(context);
        }
    }

    private static AnalysisContentItemResponse ToItemResponse(AnalysisContentItemResult result)
        => new() { Item = result.Item, Version = result.Version };

    private static AnalysisContentVersionResponse ToVersionResponse(AnalysisContentVersionResult result)
        => new() { Item = result.Item, Version = result.Version };

    private static AnalysisContentJobResponse ToJobResponse(AnalysisContentJobResult result)
        => new()
        {
            JobId = result.Job.OperationId,
            Status = result.Job.Status,
            Version = result.Version
        };

    private static bool TryMapException(Exception ex, HttpContext context, out IResult problem)
    {
        switch (ex)
        {
            case AnalysisContentValidationException validationEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status400BadRequest,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                    validationEx.Message);
                return true;
            case AnalysisContentNotFoundException notFoundEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    notFoundEx.Message);
                return true;
            case AnalysisContentConflictException conflictEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status409Conflict,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                    conflictEx.Message);
                return true;
            case AnalysisContentStoreUnavailableException storeEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                    storeEx.Message);
                return true;
            case GeoprocessingAuthorizationException authEx when authEx.RequiresAuthentication:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status401Unauthorized,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status401Unauthorized),
                    authEx.Message);
                return true;
            case GeoprocessingAuthorizationException authEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status403Forbidden,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status403Forbidden),
                    authEx.Message);
                return true;
            case GeoprocessingNotFoundException notFoundEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    notFoundEx.Message);
                return true;
            case GeoprocessingPreconditionFailedException preconditionEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status409Conflict,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                    preconditionEx.Message);
                return true;
            case GeoprocessingValidationException validationEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status400BadRequest,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                    validationEx.Message);
                return true;
            case GeoprocessingStoreUnavailableException storeEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                    storeEx.Message);
                return true;
            default:
                problem = Results.Empty;
                return false;
        }
    }

    private static IResult CreateGenericProblem(HttpContext context)
        => ProblemDetailsHelpers.CreateAdminProblem(
            context,
            StatusCodes.Status500InternalServerError,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status500InternalServerError),
            "An internal error occurred while processing the analysis content request.");

    private static void LogEndpointFailed(HttpContext context, string operation, Exception exception)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<AnalysisContentEndpointsLog>>();
        Log.EndpointFailed(logger, operation, exception);
    }

    internal sealed class AnalysisContentEndpointsLog;

    private static partial class Log
    {
        [LoggerMessage(12020, LogLevel.Error, "Analysis content endpoint {Operation} failed")]
        public static partial void EndpointFailed(
            ILogger logger,
            string operation,
            Exception exception);
    }
}

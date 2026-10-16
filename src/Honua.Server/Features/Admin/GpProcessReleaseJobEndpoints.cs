// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.CustomCode;
using Honua.Geoprocessing.ProcessRelease;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>HTTP adapter for the canonical release-bound custom-code submission path.</summary>
public static class GpProcessReleaseJobEndpoints
{
    /// <summary>Maps submission without exposing server-owned execution or credential fields.</summary>
    public static void MapGpProcessReleaseJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/gp/process-releases")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Geoprocessing")
            .RequireAdminAuthorization();

        group.MapPost("/bindings/{process}/jobs", HandleSubmit)
            .WithName("SubmitGpProcessReleaseJob")
            .WithSummary("Submit a custom-code job against an active process release")
            .Produces<GpProcessReleaseJobResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> HandleSubmit(
        string process,
        [FromBody] GpProcessReleaseJobRequest request,
        [FromServices] IGeoprocessingJobService service,
        HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrWhiteSpace(process) || request.Params.ValueKind != JsonValueKind.Object)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "A process name and params JSON object are required.");
        }

        if (request.DeclaredScope is null || request.DeclaredScope.Any(scope => scope is null
            || string.IsNullOrWhiteSpace(scope.ServiceId) || scope.Access is not ("read" or "write")))
        {
            return Problem(context, StatusCodes.Status400BadRequest,
                "declaredScope must contain serviceId, optional layerId, and access of read or write.");
        }

        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count > 1 || (keys.Count == 1 && (string.IsNullOrWhiteSpace(keys[0]) || keys[0]!.Length > 256)))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "Idempotency-Key must be one non-empty value of at most 256 characters.");
        }

        var storage = context.RequestServices.GetService<ICloudFileStorage>();
        if (storage is null || storage.Provider is not (CloudStorageProvider.AwsS3 or CloudStorageProvider.AzureBlob))
        {
            return Problem(context, StatusCodes.Status503ServiceUnavailable, "Custom-code jobs require durable cloud artifact storage on the serving host.");
        }

        // Custom-code plans carry resource scope, not a catalog process to execute. The
        // shared service recognizes the logical process metadata and applies the custom-code
        // permission, release, workload, owner-scope, admission and token gates atomically.
        // Stable plan identity is essential for replay after the live binding changes.
        var plan = new AnalysisPlan
        {
            PlanId = "release:" + process.Trim(),
            IntentId = "release:" + process.Trim(),
            Steps = [new AnalysisPlanStep { StepId = "execute", Kind = AnalysisPlanStepKind.Geoprocess }],
        };
        var metadata = new Dictionary<string, string>
        {
            [GpProcessReleaseAdmission.ProcessParam] = process.Trim(),
            [CustomCodeJobContract.ParamsJsonParam] = request.Params.GetRawText(),
            [CustomCodeJobContract.DeclaredScopeParam] = JsonSerializer.Serialize(
                request.DeclaredScope, GpProcessReleaseJsonContext.Default.IReadOnlyListGpProcessReleaseJobScope),
        };

        try
        {
            var job = await service.SubmitJobAsync(plan, keys.Count == 0 ? null : keys[0], context.User,
                metadata, context.RequestAborted).ConfigureAwait(false);
            var statusUrl = "/api/v1/admin/jobs/" + Uri.EscapeDataString(job.OperationId);
            context.Response.Headers.Location = statusUrl;
            return Results.Json(new GpProcessReleaseJobResponse
            {
                JobId = job.OperationId,
                StatusUrl = statusUrl,
                DiagnosticsUrl = statusUrl + "/diagnostics",
                ArtifactsUrl = statusUrl + "/artifacts",
            }, GpProcessReleaseJsonContext.Default.GpProcessReleaseJobResponse,
                statusCode: StatusCodes.Status202Accepted);
        }
        catch (GeoprocessingAuthorizationException exception)
        {
            return Problem(context, exception.RequiresAuthentication ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden,
                exception.Message);
        }
        catch (GeoprocessingValidationException exception)
        {
            return Problem(context, StatusCodes.Status400BadRequest, exception.Message);
        }
        catch (GeoprocessingApprovalRequiredException exception)
        {
            return GeoprocessingProblemDetailsHelpers.ApprovalRequired(context, exception);
        }
        catch (GeoprocessingIdempotencyConflictException exception)
        {
            return GeoprocessingProblemDetailsHelpers.IdempotencyConflict(context, exception);
        }
        catch (GeoprocessingAdmissionException exception)
        {
            return GeoprocessingProblemDetailsHelpers.Admission(context, exception);
        }
        catch (GeoprocessingStoreUnavailableException exception)
        {
            return GeoprocessingProblemDetailsHelpers.StoreUnavailable(context, exception);
        }
    }

    private static IResult Problem(HttpContext context, int status, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, status, ProblemDetailsHelpers.GetTitle(status), detail);
}

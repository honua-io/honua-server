// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.AgentWorkspace;

internal static class DatasetImportSubmissionEndpoints
{
    internal static void MapDatasetImportSubmissions(this IEndpointRouteBuilder group)
    {
        var services = ((IEndpointRouteBuilder)group).ServiceProvider.GetService<IServiceProviderIsService>();
        if (services?.IsService(typeof(DatasetImportSubmissionService)) != true) return;
        var submission = group.MapGroup("/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision:long}/submission")
            .RequireAuthorization();
        submission.MapGet("", (string workspaceId, string taskId, long revision, string contentHash, HttpContext context) =>
            ReadAsync(context, new(workspaceId, taskId, revision, contentHash)));
        submission.MapPost("", async (string workspaceId, string taskId, long revision, HttpContext context) =>
        {
            var parsed = await DatasetImportTaskEndpoints.ReadBoundedAsync(context, AgentWorkspaceJsonContext.Default.SubmitDatasetImport).ConfigureAwait(false);
            return parsed.Error ?? await SubmitAsync(context, new(workspaceId, taskId, revision, parsed.Request!)).ConfigureAwait(false);
        }).Accepts<SubmitDatasetImport>("application/json");
    }

    internal static async Task<IResult> ReadAsync(HttpContext context, ReadDatasetImportAuthority? request)
    {
        if (request is null || !DatasetImportTaskState.ValidAuthorityScope(request.WorkspaceId, request.TaskId, request.Revision, request.ContentHash))
            return Problem(context, 400, "A valid immutable dataset task binding is required.");
        return await ExecuteAsync(context, service => service.ReadAsync(context, request)).ConfigureAwait(false);
    }

    internal static async Task<IResult> SubmitAsync(HttpContext context, SubmitDatasetImportCall? request)
    {
        if (request?.Request is not { } input || !DatasetImportTaskState.ValidAuthorityScope(request.WorkspaceId, request.TaskId, request.Revision, input.TaskContentHash) ||
            input.AuthorityRevision <= 0 || !DatasetImportTaskState.Hash(input.AuthorityFingerprint) || !DatasetImportTaskState.Hash(input.PolicyContextFingerprint))
            return Problem(context, 400, "A valid original task and reviewed authority binding are required.");
        return await ExecuteAsync(context, service => service.SubmitAsync(context, request)).ConfigureAwait(false);
    }

    private static async Task<IResult> ExecuteAsync(HttpContext context, Func<DatasetImportSubmissionService, Task<DatasetImportSubmissionReceipt?>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        var service = context.RequestServices.GetService<DatasetImportSubmissionService>();
        if (service is null) return Results.NotFound();
        try
        {
            var receipt = await action(service).ConfigureAwait(false);
            return receipt is null ? Results.NotFound() : Results.Json(receipt, AgentWorkspaceJsonContext.Default.DatasetImportSubmissionReceipt);
        }
        catch (UnauthorizedAccessException) { return Results.NotFound(); }
        catch (AgentWorkspaceConflictException) { return Problem(context, 409, "The original task or reviewed authority changed. Read its original submission status before continuing."); }
        catch (ArgumentException) { return Problem(context, 400, "A valid immutable dataset import request is required."); }
        catch (AgentWorkspaceUnavailableException) { return Problem(context, 503, "Durable task-bound import admission is unavailable."); }
        catch (InvalidOperationException) { return Problem(context, 409, "The original import could not be admitted under current policy. Read its original status before continuing."); }
        catch (IOException) { return Problem(context, 503, "The submission outcome is unknown. Read the original status before retrying."); }
        catch (StackExchange.Redis.RedisException) { return Problem(context, 503, "The original submission status is unavailable. No acceptance is inferred."); }
    }

    private static IResult Problem(HttpContext context, int status, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, status, detail);
}

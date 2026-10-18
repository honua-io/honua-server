// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Import.Abstractions;

namespace Honua.Server.Features.AgentWorkspace;

internal static class DatasetImportTaskEndpoints
{
    internal static void MapDatasetImportTasks(this RouteGroupBuilder group)
    {
        group.MapPost("/workspaces/{workspaceId}/dataset-import-tasks", Create)
            .Accepts<CreateDatasetImportTask>("application/json");
        group.MapGet("/workspaces/{workspaceId}/dataset-import-task-intents/{intentId}", DatasetImportTaskService.ReadIntent);
        group.MapGet("/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision:long}", DatasetImportTaskService.ReadVersion);
        var capabilities = ((IEndpointRouteBuilder)group).ServiceProvider.GetService<IServiceProviderIsService>();
        if (capabilities is null || !capabilities.IsService(typeof(IDatasetImportAuthorityDescriptorReader))) return;
        group.MapGet("/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision:long}/authority",
            (string workspaceId, string taskId, long revision, string contentHash, HttpContext context) =>
                ReadAuthorityAsync(context, new(workspaceId, taskId, revision, contentHash)));
        if (capabilities.IsService(typeof(Honua.Core.Features.Authorization.Abstractions.IAgentJobSecurityContextCapture)))
            group.MapPost("/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision:long}/authority-renewals", RenewAuthority)
                .Accepts<RenewDatasetImportAuthority>("application/json");
    }

    private static async Task<IResult> Create(string workspaceId, HttpContext context, IAgentWorkspaceStore store)
    {
        var parsed = await ReadBoundedAsync(context, AgentWorkspaceJsonContext.Default.CreateDatasetImportTask).ConfigureAwait(false);
        if (parsed.Error is not null) return parsed.Error;
        return await DatasetImportTaskService.CreateAsync(workspaceId, context, store, parsed.Request!).ConfigureAwait(false);
    }

    internal static async Task<IResult> ReadAuthorityAsync(HttpContext context, ReadDatasetImportAuthority? request)
    {
        if (request is null || !DatasetImportTaskState.ValidAuthorityScope(request.WorkspaceId, request.TaskId, request.Revision, request.ContentHash))
            return Results.BadRequest(new AgentWorkspaceError("A valid immutable import task authority binding is required."));
        var reader = context.RequestServices.GetService<IDatasetImportAuthorityDescriptorReader>();
        if (reader is null) return Results.NotFound();
        var descriptor = await reader.ReadAsync(context.User, request.WorkspaceId, request.TaskId, request.Revision,
            request.ContentHash, context.RequestAborted).ConfigureAwait(false);
        return descriptor is null ? Results.NotFound() : Results.Json(descriptor, AgentWorkspaceJsonContext.Default.DatasetImportAuthorityDescriptor);
    }

    private static async Task<IResult> RenewAuthority(string workspaceId, string taskId, long revision, HttpContext context, IAgentWorkspaceStore store)
    {
        var parsed = await ReadBoundedAsync(context, AgentWorkspaceJsonContext.Default.RenewDatasetImportAuthority).ConfigureAwait(false);
        if (parsed.Error is not null) return parsed.Error;
        return await RenewAuthorityAsync(context, store, new(workspaceId, taskId, revision, parsed.Request!)).ConfigureAwait(false);
    }

    internal static async Task<IResult> RenewAuthorityAsync(HttpContext context, IAgentWorkspaceStore store, RenewDatasetImportAuthorityCall? call)
    {
        if (call is null || call.Request is not { } request || !DatasetImportTaskState.ValidRenewal(request) ||
            !DatasetImportTaskState.ValidAuthorityScope(call.WorkspaceId, call.TaskId, call.Revision, request.TaskContentHash))
            return Results.BadRequest(new AgentWorkspaceError("A valid immutable import authority renewal request is required."));
        var reader = context.RequestServices.GetService<IDatasetImportAuthorityDescriptorReader>();
        if (reader is null || await reader.ReadAsync(context.User, call.WorkspaceId, call.TaskId, call.Revision,
                call.Request.TaskContentHash, context.RequestAborted).ConfigureAwait(false) is null) return Results.NotFound();
        // Do not compare the current descriptor revision here: recovery must return a
        // persisted original renewal even if a later renewal already advanced authority.
        var receipt = await DatasetImportAuthorityRenewalService.RenewAsync(context, store, call.WorkspaceId,
            call.TaskId, call.Revision, call.Request).ConfigureAwait(false);
        return receipt is null ? Results.NotFound() : Results.Json(receipt, AgentWorkspaceJsonContext.Default.DatasetImportAuthorityRenewal);
    }

    private static async Task<(T? Request, IResult? Error)> ReadBoundedAsync<T>(HttpContext context, JsonTypeInfo<T> type) where T : class
    {
        // Explicit bounded read before deserialization also covers unknown-length/chunked bodies.
        const int maximumBytes = 65536;
        if (!context.Request.HasJsonContentType()) return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        if (context.Request.ContentLength > maximumBytes) return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        var bytes = new byte[maximumBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await context.Request.Body.ReadAsync(bytes.AsMemory(length), context.RequestAborted).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length > maximumBytes) return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        T? request;
        try { request = JsonSerializer.Deserialize(bytes.AsSpan(0, length), type); }
        catch (JsonException) { return (null, Results.BadRequest(new AgentWorkspaceError("A valid typed dataset import request is required."))); }
        return request is null ? (null, Results.BadRequest(new AgentWorkspaceError("A typed dataset import request is required."))) : (request, null);
    }
}

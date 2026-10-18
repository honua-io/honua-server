// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.AgentWorkspace;

internal static class DatasetImportTaskEndpoints
{
    internal static void MapDatasetImportTasks(this RouteGroupBuilder group)
    {
        group.MapPost("/workspaces/{workspaceId}/dataset-import-tasks", Create)
            .Accepts<CreateDatasetImportTask>("application/json");
        group.MapGet("/workspaces/{workspaceId}/dataset-import-task-intents/{intentId}", DatasetImportTaskService.ReadIntent);
        group.MapGet("/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision:long}", DatasetImportTaskService.ReadVersion);
    }

    private static async Task<IResult> Create(string workspaceId, HttpContext context, IAgentWorkspaceStore store)
    {
        // Explicit bounded read before deserialization also covers unknown-length/chunked bodies.
        const int maximumBytes = 65536;
        if (!context.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength > maximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var bytes = new byte[maximumBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await context.Request.Body.ReadAsync(bytes.AsMemory(length), context.RequestAborted).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length > maximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        CreateDatasetImportTask? request;
        try { request = JsonSerializer.Deserialize(bytes.AsSpan(0, length), AgentWorkspaceJsonContext.Default.CreateDatasetImportTask); }
        catch (JsonException) { return Results.BadRequest(new AgentWorkspaceError("A valid typed dataset import task is required.")); }
        if (request is null) return Results.BadRequest(new AgentWorkspaceError("A typed dataset import task is required."));
        return await DatasetImportTaskService.CreateAsync(workspaceId, context, store, request).ConfigureAwait(false);
    }
}

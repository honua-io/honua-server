// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.AgentWorkspace;

internal static class DatasetImportTaskService
{
    internal static async Task<IResult> CreateAsync(string workspaceId, HttpContext context, IAgentWorkspaceStore store, CreateDatasetImportTask request)
    {
        DatasetImportTaskState.Validate(request);
        var workspace = await ReadWorkspace(workspaceId, context, store).ConfigureAwait(false);
        var actor = CanonicalSecurityActor.Resolve(context.User)!.ActorId;
        var key = DatasetImportTaskState.IntentKey(actor, request.IntentId);
        var fingerprint = DatasetImportTaskState.RequestHash(request);
        if (workspace.DatasetImportTaskIntents.TryGetValue(key, out var existing))
        {
            if (existing.ActorId != actor || existing.IntentId != request.IntentId || existing.RequestHash != fingerprint)
                throw new AgentWorkspaceConflictException("The original import intent has different immutable inputs.");
            return await AuthorizedVersion(context, workspace, existing.TaskId, existing.Revision).ConfigureAwait(false);
        }
        AgentWorkspaceCapturedContext.RequireOriginal(context, workspace, request.ViewId, request.ContextRevision);
        var current = AgentWorkspaceState.Current(workspace, request.ViewId);
        if (current.Revision != request.ContextRevision)
            throw new AgentWorkspaceConflictException("The workspace context changed. Review the import destination before creating a task.");
        if (workspace.DatasetImportTasks.Count >= 128 || workspace.DatasetImportTaskIntents.Count >= 256)
            throw new AgentWorkspaceConflictException("The workspace import task limit was reached.");
        var destination = context.RequestServices.GetRequiredService<IDatasetImportDestinationAuthorizer>();
        if (!await destination.IsAuthorizedAsync(context.User, workspace.TargetId, request.Destination, request.Options,
                true, context.RequestAborted).ConfigureAwait(false) ||
            !await DatasetImportTaskProvider.SourceAuthorizedAsync(context, workspace, request.Source, context.RequestAborted).ConfigureAwait(false))
            return Results.Forbid();
        if (!await AgentCredentialAuthority.IsCurrentAsync(context.User, context.RequestServices, context.RequestAborted).ConfigureAwait(false))
            return Results.Forbid();
        var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var task = new DatasetImportTask($"import-task-{Guid.NewGuid():N}", 1, string.Empty, actor,
            workspace.TenantId, workspace.TargetId, workspace.WorkspaceId, request.Destination, request.Source,
            request.Options, now.AddHours(24));
        task = task with { ContentHash = DatasetImportTaskState.ContentHash(task) };
        var tasks = new Dictionary<string, DatasetImportTask[]>(workspace.DatasetImportTasks, StringComparer.Ordinal) { [task.TaskId] = [task] };
        var intents = new Dictionary<string, DatasetImportTaskIntent>(workspace.DatasetImportTaskIntents, StringComparer.Ordinal)
        { [key] = new(actor, request.IntentId, fingerprint, task.TaskId, task.Revision) };
        var next = AgentWorkspaceState.Update(workspace with { DatasetImportTasks = tasks, DatasetImportTaskIntents = intents },
            new(request.ViewId, request.ContextRevision, workspace.TargetId, current.SelectedResources,
                new(task.TaskId, task.Revision, "dataset-import", ContentHash: task.ContentHash), "form"));
        if (!await store.SaveAsync(next).ConfigureAwait(false))
            throw new AgentWorkspaceConflictException("The workspace changed. Read the original import intent before retrying.");
        return Results.Json(task, AgentWorkspaceJsonContext.Default.DatasetImportTask);
    }

    internal static async Task<IResult> ReadIntent(string workspaceId, string intentId, HttpContext context, IAgentWorkspaceStore store)
    {
        if (!AgentWorkspaceState.IsIdentifier(intentId)) throw new ArgumentException("A valid original intent is required.");
        var workspace = await ReadWorkspace(workspaceId, context, store).ConfigureAwait(false);
        var actor = CanonicalSecurityActor.Resolve(context.User)!.ActorId;
        var key = DatasetImportTaskState.IntentKey(actor, intentId);
        if (!workspace.DatasetImportTaskIntents.TryGetValue(key, out var intent) || intent.ActorId != actor || intent.IntentId != intentId)
            return Results.NotFound();
        return await AuthorizedVersion(context, workspace, intent.TaskId, intent.Revision).ConfigureAwait(false);
    }

    internal static async Task<IResult> ReadVersion(string workspaceId, string taskId, long revision, HttpContext context, IAgentWorkspaceStore store)
        => await AuthorizedVersion(context, await ReadWorkspace(workspaceId, context, store).ConfigureAwait(false), taskId, revision).ConfigureAwait(false);

    private static async Task<IResult> AuthorizedVersion(HttpContext context, AgentWorkspaceDocument workspace, string taskId, long revision)
    {
        if (!workspace.DatasetImportTasks.TryGetValue(taskId, out var versions)) return Results.NotFound();
        var candidate = versions.SingleOrDefault(task => task.Revision == revision);
        if (candidate is null) return Results.NotFound();
        var task = await context.RequestServices.GetRequiredService<IDatasetImportTaskProvider>().AuthorizeAsync(context.User,
            workspace.WorkspaceId, taskId, revision, candidate.ContentHash, false, context.RequestAborted).ConfigureAwait(false);
        return task is null ? Results.NotFound() : Results.Json(task, AgentWorkspaceJsonContext.Default.DatasetImportTask);
    }

    private static async Task<AgentWorkspaceDocument> ReadWorkspace(string id, HttpContext context, IAgentWorkspaceStore store)
    {
        if (!AgentWorkspaceState.IsIdentifier(id)) throw new ArgumentException("A valid workspace is required.");
        var actor = CanonicalSecurityActor.Resolve(context.User)?.ActorId;
        if (actor is null || !await AgentCredentialAuthority.IsCurrentAsync(context.User, context.RequestServices, context.RequestAborted).ConfigureAwait(false))
            throw new AgentWorkspaceNotFoundException();
        var workspace = await store.GetAsync(id).ConfigureAwait(false);
        if (workspace is null || !AgentWorkspaceState.CanRead(workspace, actor,
            context.RequestServices.GetService<ITenantContext>()?.TenantId, await store.GetTargetIdAsync().ConfigureAwait(false)))
            throw new AgentWorkspaceNotFoundException();
        return workspace;
    }
}

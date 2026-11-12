// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Assets;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskService(
    IWorkTaskStore store,
    WorkTaskLocator locator,
    WorkTaskFormBinding formBinding)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;
    private const int MaxTitleLength = 300;
    private const int MaxPriorityLength = 40;

    public async Task<IResult> ListAsync(HttpContext context)
    {
        var query = context.Request.Query;
        var grant = await AssetServiceAccess.RequireEditorAsync(context, query["serviceId"].ToString()).ConfigureAwait(false);
        if (grant.Denied is { } denied)
        {
            return denied;
        }

        var status = Optional(query["status"]);
        if (status is not null && !WorkTaskStatus.All.Contains(status, StringComparer.Ordinal))
        {
            return AssetCommandHttp.Rejected(context, "status", $"status must be one of: {string.Join(", ", WorkTaskStatus.All)}.");
        }

        if (Cursor(query["cursor"]) is not long after)
        {
            return AssetCommandHttp.Rejected(context, "cursor", "cursor is not valid.");
        }

        var result = await store.ListAsync(
            new WorkTaskQuery
            {
                ServiceId = grant.ServiceId,
                Status = status,
                Assignee = Optional(query["assignee"]),
                Type = Optional(query["type"]),
                AfterObjectId = after,
                Limit = Limit(query["limit"]),
            },
            context.RequestAborted).ConfigureAwait(false);
        var actor = AssetCommandHttp.ResolveTaskActor(context);
        AssetCommandHttp.ApplyNoStore(context.Response);
        return Results.Json(
            result with { Items = [.. result.Items.Select(task => WithPermittedActions(task, actor))] },
            AssetsJsonContext.Default.WorkTaskListResult);
    }

    public async Task<IResult> GetAsync(HttpContext context, Guid taskId)
    {
        var task = await FindVisibleAsync(context, taskId).ConfigureAwait(false);
        if (task is null)
        {
            return NotFound(context);
        }

        var actor = AssetCommandHttp.ResolveTaskActor(context);
        AssetCommandHttp.ApplyNoStore(context.Response);
        return Results.Json(WithPermittedActions(task, actor), AssetsJsonContext.Default.WorkTask);
    }

    public async Task<IResult> HistoryAsync(HttpContext context, Guid taskId)
    {
        if (await FindVisibleAsync(context, taskId).ConfigureAwait(false) is null)
        {
            return NotFound(context);
        }

        var query = context.Request.Query;
        if (Cursor(query["cursor"]) is not long after)
        {
            return AssetCommandHttp.Rejected(context, "cursor", "cursor is not valid.");
        }

        var history = await store.ListHistoryAsync(new WorkTaskHistoryQuery(taskId, after, Limit(query["limit"])), context.RequestAborted)
            .ConfigureAwait(false);
        AssetCommandHttp.ApplyNoStore(context.Response);
        return Results.Json(history, AssetsJsonContext.Default.WorkTaskHistoryResult);
    }

    public async Task<IResult> CreateAsync(HttpContext context)
    {
        var body = await AssetCommandHttp.ReadAsync(context, AssetsJsonContext.Default.WorkTaskCreateRequest).ConfigureAwait(false);
        if (body is not { CommandId: Guid commandId, TaskId: Guid taskId })
        {
            return AssetCommandHttp.Rejected(context, "commandId", "A JSON body with commandId and a client-generated taskId is required.");
        }

        var grant = await AssetServiceAccess.RequireEditorAsync(context, body.ServiceId).ConfigureAwait(false);
        if (grant.Denied is { } denied)
        {
            return denied;
        }

        var request = body with { ServiceId = grant.ServiceId };
        var actor = AssetCommandHttp.ResolveTaskActor(context);
        var command = AssetCommandHttp.CreateCommand(
            context,
            new CommandSubject(commandId, grant.ServiceId, WorkTaskAction.Create, taskId),
            (request, AssetsJsonContext.Default.WorkTaskCreateRequest));
        if (await store.FindReceiptAsync(command, context.RequestAborted).ConfigureAwait(false) is { } replay)
        {
            return AssetCommandHttp.ToResult(context, replay, StatusCodes.Status201Created);
        }

        if (!MayCreate(request, actor))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status403Forbidden, "Only planners may create assigned or non-request tasks.");
        }

        if (ValidateCreate(request) is { } invalid)
        {
            return AssetCommandHttp.Rejected(context, [invalid]);
        }

        var location = await locator.LocateAsync(request, context.User, context.RequestAborted).ConfigureAwait(false);
        var (form, formError) = await formBinding.PinAsync(request, grant.Service!, context.RequestAborted).ConfigureAwait(false);
        AssetFieldError[] errors = [.. new[] { location.Error, formError }.OfType<AssetFieldError>()];
        if (errors.Length > 0)
        {
            return AssetCommandHttp.Rejected(context, errors);
        }

        var result = await store.CreateAsync(
            new WorkTaskDraft(NewTask(request, new WorkTaskPlacement(location, form!), actor), location.Wkb!, location.Srid),
            command,
            context.RequestAborted).ConfigureAwait(false);
        return AssetCommandHttp.ToResult(context, result, StatusCodes.Status201Created);
    }

    public async Task<IResult> TransitionAsync(HttpContext context, Guid taskId)
    {
        var request = await AssetCommandHttp.ReadAsync(context, AssetsJsonContext.Default.WorkTaskTransitionRequest).ConfigureAwait(false);
        if (request is not { CommandId: Guid commandId, ExpectedVersion: long })
        {
            return AssetCommandHttp.Rejected(context, "expectedVersion", "A JSON body with commandId and expectedVersion is required.");
        }

        if (await FindVisibleAsync(context, taskId).ConfigureAwait(false) is not { } task)
        {
            return NotFound(context);
        }

        var actor = AssetCommandHttp.ResolveTaskActor(context);
        var command = AssetCommandHttp.CreateCommand(
            context,
            new CommandSubject(commandId, task.ServiceId, request.Action ?? string.Empty, taskId),
            (request, AssetsJsonContext.Default.WorkTaskTransitionRequest));
        var result = await store.TransitionAsync(new WorkTaskTransitionCommand(taskId, request, actor, command), context.RequestAborted).ConfigureAwait(false);
        return AssetCommandHttp.ToResult(context, result, StatusCodes.Status200OK);
    }

    private static WorkTask NewTask(WorkTaskCreateRequest request, WorkTaskPlacement placement, WorkTaskActor actor)
    {
        var assignee = Optional(request.Assignee);
        return new WorkTask
        {
            TaskId = request.TaskId!.Value,
            ServiceId = request.ServiceId!,
            Type = request.Type!,
            Status = assignee is null ? WorkTaskStatus.Unassigned : WorkTaskStatus.Assigned,
            Version = 1,
            ProfileId = request.ProfileId,
            AssetId = placement.Location.AssetId,
            GeometrySource = placement.Location.Source!,
            Title = request.Title!.Trim(),
            Assignee = assignee,
            Priority = Optional(request.Priority),
            DueAt = request.DueAt,
            Form = placement.Form,
            CreatedBy = actor.Id,
        };
    }

    // Crew may report new work; planning it (type, assignee) stays with planners.
    private static bool MayCreate(WorkTaskCreateRequest request, WorkTaskActor actor)
        => actor.IsPlanner || (request.Type == WorkTaskType.Request && Optional(request.Assignee) is null);

    private static AssetFieldError? ValidateCreate(WorkTaskCreateRequest request)
    {
        if (!WorkTaskType.All.Contains(request.Type ?? string.Empty, StringComparer.Ordinal))
        {
            return new AssetFieldError("type", $"type must be one of: {string.Join(", ", WorkTaskType.All)}.");
        }

        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > MaxTitleLength)
        {
            return new AssetFieldError("title", $"title is required and at most {MaxTitleLength} characters.");
        }

        if (request.Priority is { Length: > MaxPriorityLength })
        {
            return new AssetFieldError("priority", $"priority is at most {MaxPriorityLength} characters.");
        }

        return request.Type == WorkTaskType.Inspection && string.IsNullOrWhiteSpace(request.AssetId)
            ? new AssetFieldError("assetId", "An inspection task needs an asset.")
            : null;
    }

    // Tasks of a service the caller cannot edit are reported as missing, never as forbidden.
    private async Task<WorkTask?> FindVisibleAsync(HttpContext context, Guid taskId)
    {
        var task = await store.GetAsync(taskId, context.RequestAborted).ConfigureAwait(false);
        return task is null || (await AssetServiceAccess.RequireEditorAsync(context, task.ServiceId).ConfigureAwait(false)).Denied is not null
            ? null
            : task;
    }

    private static WorkTask WithPermittedActions(WorkTask task, WorkTaskActor actor)
        => task with { PermittedActions = WorkTaskStateMachine.PermittedActions(task, actor) };

    private static long? Cursor(string? value)
        => string.IsNullOrEmpty(value) ? 0 : long.TryParse(value, out var cursor) && cursor >= 0 ? cursor : null;

    private static int Limit(string? value) => int.TryParse(value, out var limit) ? Math.Clamp(limit, 1, MaxLimit) : DefaultLimit;

    private static IResult NotFound(HttpContext context)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "The task was not found.");

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record WorkTaskPlacement(WorkTaskLocation Location, WorkTaskFormReference Form);

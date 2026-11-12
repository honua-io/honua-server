// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Assets;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Assets;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskCompletionService(
    IWorkTaskStore tasks,
    IWorkTaskCompletionStore completions,
    TimeProvider clock)
{
    private const int MaxNotesLength = 4000;
    private const int MinCondition = 1;
    private const int MaxCondition = 5;

    // Field devices drift; an observation a few minutes ahead of the server is accepted, later ones are not.
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromMinutes(5);

    public async Task<IResult> CompleteAsync(HttpContext context, Guid taskId)
    {
        var request = await AssetCommandHttp.ReadAsync(context, AssetsJsonContext.Default.WorkTaskCompletionRequest).ConfigureAwait(false);
        if (request is not { CommandId: Guid commandId, ExpectedVersion: long })
        {
            return AssetCommandHttp.Rejected(context, "expectedVersion", "A JSON body with commandId and expectedVersion is required.");
        }

        var task = await tasks.GetAsync(taskId, context.RequestAborted).ConfigureAwait(false);
        if (task is null ||
            (await AssetServiceAccess.RequireEditorAsync(context, task.ServiceId).ConfigureAwait(false)).Denied is not null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "The task was not found.");
        }

        var command = AssetCommandHttp.CreateCommand(
            context,
            new CommandSubject(commandId, task.ServiceId, WorkTaskAction.Complete, taskId),
            (request, AssetsJsonContext.Default.WorkTaskCompletionRequest));
        if (await tasks.FindReceiptAsync(command, context.RequestAborted).ConfigureAwait(false) is { } replay)
        {
            return AssetCommandHttp.ToResult(context, replay, StatusCodes.Status200OK);
        }

        if (Validate(request, task) is { } invalid)
        {
            return AssetCommandHttp.Rejected(context, [invalid]);
        }

        var actor = AssetCommandHttp.ResolveTaskActor(context);
        var result = await completions.CompleteAsync(new WorkTaskCompletionCommand(taskId, request, actor, command), context.RequestAborted).ConfigureAwait(false);
        return AssetCommandHttp.ToResult(context, result, StatusCodes.Status200OK);
    }

    private AssetFieldError? Validate(WorkTaskCompletionRequest request, WorkTask task)
    {
        if (request.ObservedAt is not DateTimeOffset observedAt || observedAt > clock.GetUtcNow() + ClockSkewAllowance)
        {
            return new AssetFieldError("observedAt", "observedAt is required and cannot be in the future.");
        }

        if (request.Form is null)
        {
            return new AssetFieldError("form", "The pinned form reference is required.");
        }

        if (request.Condition is int condition &&
            (task.Type != WorkTaskType.Inspection || condition is < MinCondition or > MaxCondition))
        {
            return new AssetFieldError("condition", $"condition is recorded by inspections only, from {MinCondition} to {MaxCondition}.");
        }

        if (request.Answers is { ValueKind: not JsonValueKind.Object })
        {
            return new AssetFieldError("answers", "answers must be a JSON object.");
        }

        return request.Notes is { Length: > MaxNotesLength }
            ? new AssetFieldError("notes", $"notes are at most {MaxNotesLength} characters.")
            : null;
    }
}

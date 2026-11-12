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
            (await WorkTaskAccess.RequireAsync(context, task.ServiceId, task.ProfileId).ConfigureAwait(false)).Denied is not null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "The task was not found.");
        }

        var command = AssetCommandHttp.CreateCommand(
            context,
            new CommandSubject(commandId, task.ServiceId, (WorkTaskAction.Complete, taskId)),
            (request with { SummaryRevision = null }, AssetsJsonContext.Default.WorkTaskCompletionRequest));
        if (await tasks.FindReceiptAsync(command, context.RequestAborted).ConfigureAwait(false) is { } replay)
        {
            return AssetCommandHttp.ToResult(context, replay, StatusCodes.Status200OK);
        }

        if (Validate(request, task) is { } invalid)
        {
            return AssetCommandHttp.Rejected(context, [invalid]);
        }

        var actor = AssetCommandHttp.ResolveTaskActor(context);
        var completion = new WorkTaskCompletionCommand { TaskId = taskId, Request = request, Actor = actor, Command = command, AuthorizedProfileId = task.ProfileId };
        return AssetCommandHttp.ToResult(
            context, await completions.CompleteAsync(completion, context.RequestAborted).ConfigureAwait(false), StatusCodes.Status200OK);
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

        // The summary's condition shares the inspection watermark, so every inspection must record one.
        var inspection = task.Type == WorkTaskType.Inspection;
        if (inspection != (request.Condition is not null) || request.Condition is < MinCondition or > MaxCondition)
        {
            return new AssetFieldError("condition", $"Inspections record a condition from {MinCondition} to {MaxCondition}; other tasks record none.");
        }

        if (request.Answers is { ValueKind: not JsonValueKind.Object })
        {
            return new AssetFieldError("answers", "answers must be a JSON object.");
        }

        if (request.Answers is { } answers && answers.GetRawText().Contains("\\u0000", StringComparison.OrdinalIgnoreCase))
        {
            return new AssetFieldError("answers", "answers cannot contain NUL characters.");
        }

        return request.Notes is { Length: > MaxNotesLength } || request.Notes?.Contains('\0', StringComparison.Ordinal) == true
            ? new AssetFieldError("notes", $"notes are at most {MaxNotesLength} characters and cannot contain NUL characters.")
            : null;
    }
}

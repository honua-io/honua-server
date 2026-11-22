// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Forms.Packages;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Assets;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskCompletionService(
    IWorkTaskStore tasks,
    IWorkTaskCompletionStore completions,
    AssetFormLookup forms,
    FormPackageValidator formValidator,
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

        var actor = AssetCommandHttp.ResolveTaskActor(context);
        if (!actor.IsPlanner && !actor.IsAssignee(task.Assignee))
        {
            return Forbidden(context);
        }

        if (await ValidateAllAsync(request, task, context.RequestAborted).ConfigureAwait(false) is { Length: > 0 } errors)
        {
            return AssetCommandHttp.Rejected(context, errors);
        }

        var completion = new WorkTaskCompletionCommand { TaskId = taskId, Request = request, Actor = actor, Command = command, AuthorizedProfileId = task.ProfileId };
        return AssetCommandHttp.ToResult(
            context, await completions.CompleteAsync(completion, context.RequestAborted).ConfigureAwait(false), StatusCodes.Status200OK);
    }

    private static IResult Forbidden(HttpContext context) => AssetCommandHttp.ToResult(
        context,
        new AssetCommandResult(AssetCommandStatus.Forbidden, null, "Only the assignee or a planner can complete this task."),
        StatusCodes.Status200OK);

    private async Task<AssetFieldError[]> ValidateAllAsync(
        WorkTaskCompletionRequest request,
        WorkTask task,
        CancellationToken cancellationToken)
    {
        if (Validate(request, task) is { } invalid)
        {
            return [invalid];
        }

        return await ValidateAnswersAsync(request, task, cancellationToken).ConfigureAwait(false);
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

        if (request.Answers is { } answers && ContainsNul(answers))
        {
            return new AssetFieldError("answers", "answers cannot contain NUL characters.");
        }

        return request.Notes is { Length: > MaxNotesLength } || request.Notes?.Contains('\0', StringComparison.Ordinal) == true
            ? new AssetFieldError("notes", $"notes are at most {MaxNotesLength} characters and cannot contain NUL characters.")
            : null;
    }

    private async Task<AssetFieldError[]> ValidateAnswersAsync(
        WorkTaskCompletionRequest request,
        WorkTask task,
        CancellationToken cancellationToken)
    {
        if (request.Form != task.Form)
        {
            return [new("form", $"The task is pinned to form {task.Form.FormId} version {task.Form.Version}.")];
        }

        var published = await forms.FindPinnedAsync(task, cancellationToken).ConfigureAwait(false);
        if (published is null)
        {
            return [new("form", "The pinned form version is unavailable.")];
        }

        var values = ReadAnswers(request.Answers);
        if (values is null)
        {
            return [new("answers", "Answer field names must be unique.")];
        }

        var validation = await formValidator.ValidateRecordAsync(published, (values, []), cancellationToken).ConfigureAwait(false);
        return validation.Issues
            .Where(static issue => issue.Severity == "error")
            .Select(static issue => new AssetFieldError(issue.FieldId is { } field ? $"answers.{field}" : issue.Path ?? "form", issue.Message))
            .ToArray();
    }

    private static Dictionary<string, JsonElement>? ReadAnswers(JsonElement? answers)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (answers is not { } supplied)
        {
            return values;
        }

        foreach (var answer in supplied.EnumerateObject())
        {
            if (!values.TryAdd(answer.Name, answer.Value))
            {
                return null;
            }
        }

        return values;
    }

    private static bool ContainsNul(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()?.Contains('\0', StringComparison.Ordinal) == true,
        JsonValueKind.Array => element.EnumerateArray().Any(ContainsNul),
        JsonValueKind.Object => element.EnumerateObject().Any(static property =>
            property.Name.Contains('\0', StringComparison.Ordinal) || ContainsNul(property.Value)),
        _ => false,
    };
}

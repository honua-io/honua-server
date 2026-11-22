// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Assets;
using Honua.Server.Features.Assets;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskCompletionProcessor(WorkTaskCompletionForm form, WorkTaskCompletionExecution execution, TimeProvider clock)
{
    private const int MaxNotesLength = 4000;
    private const int MinCondition = 1;
    private const int MaxCondition = 5;
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromMinutes(5);

    public async Task<IResult> CompleteAsync(HttpContext context,
        (WorkTask Task, WorkTaskCompletionRequest Request, AssetCommand Command, long Generation) completion)
    {
        var (task, request, command, _) = completion;
        if (Validate(request, task) is { } invalid)
        {
            return AssetCommandHttp.Rejected(context, [invalid]);
        }
        if (await form.ValidateAsync((task, request, command.Actor), context.RequestAborted).ConfigureAwait(false) is { Length: > 0 } errors)
        {
            return AssetCommandHttp.Rejected(context, errors);
        }
        return await execution.ApplyAsync(context, completion).ConfigureAwait(false);
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

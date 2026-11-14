// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Assets;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskCompletionValidation(TimeProvider clock, WorkTaskCompletionForm forms)
{
    private const int MaxNotesLength = 4000;
    private const int MinCondition = 1;
    private const int MaxCondition = 5;

    // Field devices drift; an observation a few minutes ahead of the server is accepted, later ones are not.
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromMinutes(5);

    public async Task<AssetFieldError[]> ValidateAsync(WorkTaskCompletionRequest request, WorkTask task, CancellationToken cancellationToken)
    {
        return Validate(request, task) is { } invalid
            ? [invalid]
            : await forms.ValidateAsync((task, request), cancellationToken).ConfigureAwait(false);
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

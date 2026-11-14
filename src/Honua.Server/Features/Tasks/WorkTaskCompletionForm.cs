// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Forms.Packages;
using Honua.Server.Features.Assets;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskCompletionForm(AssetFormLookup forms, FormPackageValidator validator)
{
    public async Task<AssetFieldError[]> ValidateAsync((WorkTask Task, WorkTaskCompletionRequest Request) completion, CancellationToken cancellationToken)
    {
        var (task, request) = completion;
        if (request.Form != task.Form)
        {
            return [new("form", "The completion must use the task's pinned form version.")];
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

        var result = await validator.ValidateRecordAsync(published, (values, []), cancellationToken).ConfigureAwait(false);
        return result.Issues.Where(static issue => issue.Severity == "error")
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
}

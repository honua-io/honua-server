// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Assets;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskCompletionExecution(IWorkTaskCompletionStore completions, WorkTaskCompletionWriteback writeback)
{
    public async Task<IResult> ApplyAsync(HttpContext context,
        (WorkTask Task, WorkTaskCompletionRequest Request, AssetCommand Command, long Generation) completion)
    {
        var (task, request, command, configurationGeneration) = completion;
        var preparation = await writeback.PrepareAsync(context, (task, request), context.RequestAborted).ConfigureAwait(false);
        if (preparation.Error is { } error)
        {
            return AssetCommandHttp.Rejected(context, [error]);
        }

        var actor = AssetCommandHttp.ResolveTaskActor(context);
        using var scope = FeatureMutationOutboxScope.BeginIfNotNull(preparation.Scope);
        var result = await completions.CompleteAsync(new WorkTaskCompletionCommand(task.TaskId, request, actor)
        {
            Command = command,
            ConfigurationGeneration = configurationGeneration,
            AssetWriteback = preparation.Plan,
        }, context.RequestAborted).ConfigureAwait(false);
        if (result.Status == AssetCommandStatus.Accepted)
        {
            await writeback.InvalidateAsync(preparation).ConfigureAwait(false);
        }
        return AssetCommandHttp.ToResult(context, result, StatusCodes.Status200OK);
    }

}

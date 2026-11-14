// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Assets;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskCompletionService(
    IWorkTaskStore tasks,
    IWorkTaskCompletionStore completions,
    WorkTaskCompletionValidation validation)
{
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
            (request, AssetsJsonContext.Default.WorkTaskCompletionRequest));
        if (await tasks.FindReceiptAsync(command, context.RequestAborted).ConfigureAwait(false) is { } replay)
        {
            return AssetCommandHttp.ToResult(context, replay, StatusCodes.Status200OK);
        }

        if (await validation.ValidateAsync(request, task, context.RequestAborted).ConfigureAwait(false) is { Length: > 0 } invalid)
        {
            return AssetCommandHttp.Rejected(context, invalid);
        }

        var actor = AssetCommandHttp.ResolveTaskActor(context);
        var result = await completions.CompleteAsync(new WorkTaskCompletionCommand { TaskId = taskId, Request = request, Actor = actor, Command = command }, context.RequestAborted).ConfigureAwait(false);
        return AssetCommandHttp.ToResult(context, result, StatusCodes.Status200OK);
    }

}
